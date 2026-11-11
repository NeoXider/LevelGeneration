using System;
using System.Collections.Generic;
using RedoSchool.LevelGeneration.Generation.Domain.Enums;
using RedoSchool.LevelGeneration.Generation.Domain.Models;

namespace RedoSchool.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Builds the logical mission graph using one generator-facing room kind.
    /// Door topology is derived from graph degree and stored in RoomNode.RequiredDoorCount.
    /// </summary>
    public sealed class MissionGraphBuilder : IMissionGraphBuilder
    {
        private readonly IInteractiveRoomKindRules _interactiveRoomKinds;

        /// <param name="interactiveRoomKinds">
        /// Interactive room types of the host game: content support, answer branches and placement hints.
        /// </param>
        public MissionGraphBuilder(IInteractiveRoomKindRules interactiveRoomKinds)
        {
            _interactiveRoomKinds = interactiveRoomKinds
                                    ?? throw new ArgumentNullException(nameof(interactiveRoomKinds));
        }

        public LevelGraph Build(IMissionConfigView config, ITilesetView tileset, IDeterministicRng rng)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (tileset == null)
            {
                throw new ArgumentNullException(nameof(tileset));
            }

            if (rng == null)
            {
                throw new ArgumentNullException(nameof(rng));
            }

            // Принудительный путь (читы «тестовый уровень»): ровно перечисленные комнаты между стартом и
            // выходом. Ветви, тупики и обязательные проходы здесь НЕ добавляются — иначе «одна
            // интерактивная комната» перестала бы быть одной. Всё остальное (выбор комнат, размещение,
            // двери, валидатор) остаётся общим конвейером.
            if (config is IForcedMainPathProvider forced && TryBuildForcedGraph(forced.ForcedMainPath, out LevelGraph forcedGraph))
            {
                return forcedGraph;
            }

            IntRange mainPathRange = ResolveRange(config.MainPathRoomsRange, tileset.MainPathRoomsRange);
            IntRange totalRange = ResolveRange(config.TotalRoomsRange, tileset.TotalRoomsRange);
            IntRange branchingRange = ResolveRange(config.BranchingRange, tileset.BranchingRange);
            IntRange branchRange = ResolveRange(config.SideBranchesRange, tileset.SideBranchesRange);
            IntRange deadEndRange = ResolveRange(config.DeadEndsRange, tileset.DeadEndsRange);
            IReadOnlyList<IInteractiveMissionContentSpec> plannedInteractiveContent =
                ResolvePlannedInteractiveContent(config);
            int plannedInteractiveCount = CountPlannedInteractivePlacements(config, plannedInteractiveContent);
            bool hasPlannedInteractiveContent = config.GenerationMode != LevelGenerationMode.Scripted
                                                && plannedInteractiveCount > 0;
            int desiredMainPathCountWithInteractive = 0;

            int mainPathCount = Clamp(
                rng.NextInt(mainPathRange.Min, mainPathRange.Max + 1),
                3,
                Math.Max(3, totalRange.Max));
            int targetTotalCount = Clamp(
                rng.NextInt(totalRange.Min, totalRange.Max + 1),
                3,
                Math.Max(3, totalRange.Max));
            int branchingLevel = Clamp(
                rng.NextInt(branchingRange.Min, branchingRange.Max + 1),
                1,
                Math.Max(1, totalRange.Max));
            int branchPointBudget = Math.Max(0, branchingLevel - 1);
            int branchRoomReserve = !hasPlannedInteractiveContent && branchPointBudget > 0
                ? Math.Min(Math.Max(0, targetTotalCount - 3),
                    Math.Max(branchPointBudget, branchRange.Min + deadEndRange.Min))
                : 0;

            if (hasPlannedInteractiveContent)
            {
                int baseTargetTotalCount = targetTotalCount;
                int baseBranchReserve = branchPointBudget > 0
                    ? Math.Min(
                        Math.Max(0, baseTargetTotalCount - 3),
                        Math.Max(branchPointBudget, branchRange.Min + deadEndRange.Min))
                    : 0;

                if (branchPointBudget == 0)
                {
                    mainPathCount = Math.Max(mainPathCount, baseTargetTotalCount);
                    baseTargetTotalCount = mainPathCount;
                }
                else
                {
                    mainPathCount = Math.Min(mainPathCount, Math.Max(3, baseTargetTotalCount - baseBranchReserve));
                    baseTargetTotalCount = Math.Max(
                        mainPathCount + branchRange.Min + deadEndRange.Min,
                        baseTargetTotalCount);
                }

                targetTotalCount = Math.Max(baseTargetTotalCount, mainPathCount + plannedInteractiveCount);
                int plannedInteractiveMainPathCap = Math.Max(8, plannedInteractiveCount * 2 + 8);
                mainPathCount = Math.Min(mainPathCount, plannedInteractiveMainPathCap);
                targetTotalCount = Math.Min(
                    targetTotalCount,
                    Math.Max(mainPathCount, plannedInteractiveCount * 4 + 12));
                desiredMainPathCountWithInteractive = mainPathCount + plannedInteractiveCount;
            }
            else if (branchPointBudget == 0)
            {
                mainPathCount = Math.Max(mainPathCount, targetTotalCount);
                targetTotalCount = mainPathCount;
            }
            else
            {
                mainPathCount = Math.Min(mainPathCount, Math.Max(3, targetTotalCount - branchRoomReserve));
                targetTotalCount = Math.Max(mainPathCount + branchRange.Min + deadEndRange.Min, targetTotalCount);
            }

            int exitNodeCount = CountExitNodes(config);
            int targetSpineCount = Math.Max(1, mainPathCount - 1 - exitNodeCount);
            List<RoomNode> nodes = new(mainPathCount + 4);
            List<LevelGraphEdge> edges = new();

            IReadOnlyList<RoomKind> required = config.RequiredRoomKinds ?? Array.Empty<RoomKind>();
            bool isScripted = config.GenerationMode == LevelGenerationMode.Scripted;
            List<SpineEntry> spine = isScripted
                ? BuildScriptedSpine(config.HybridSequence)
                : ShouldUseHybridSequence(config)
                    ? BuildHybridSpine(config.HybridSequence, required, targetSpineCount, rng)
                    : BuildSpine(config.ArchetypeKind, required, targetSpineCount);
            List<SpineEntry> plannedBranchEntries = new();
            if (!isScripted)
            {
                plannedBranchEntries = ApplyInteractiveContentToSpine(config, spine, rng);
                if (hasPlannedInteractiveContent)
                {
                    int desiredBranchHostCount = DeterminePlannedBranchHostCount(plannedBranchEntries.Count);
                    int desiredSpineCount = Math.Max(
                        Math.Max(1, mainPathCount - 1 - exitNodeCount),
                        desiredBranchHostCount * 2 + 2);
                    while (spine.Count < desiredSpineCount)
                    {
                        spine.Add(new SpineEntry(RoomKind.Passage));
                    }

                    if (spine.Count > 0 && spine[spine.Count - 1].Kind == RoomKind.Interactive)
                    {
                        spine.Add(new SpineEntry(RoomKind.Passage));
                    }

                    PreferPassagesAfterInteractiveContent(spine, config);
                    PreferStraightMainPassages(spine);
                }
            }

            mainPathCount = Math.Max(mainPathCount, spine.Count + 1 + exitNodeCount);
            targetTotalCount = Math.Max(targetTotalCount, mainPathCount);

            RoomNode startNode = new("n_start", RoomKind.Start);
            nodes.Add(startNode);

            string prevNodeId = startNode.NodeId;
            for (int i = 0; i < spine.Count; i++)
            {
                SpineEntry spec = spine[i];
                string nodeId = $"n_main_{i + 1}";
                RoomNode node = new(
                    nodeId,
                    spec.Kind,
                    isOnMainPath: true,
                    preferredRoomTemplateId: spec.PreferredRoomTemplateId,
                    contentId: spec.ContentId);
                nodes.Add(node);
                bool isMazeBranch = TryResolveMazeBranchOptionCount(config, spec.ContentId, out int optionCount);
                edges.Add(new LevelGraphEdge(
                    prevNodeId,
                    nodeId,
                    true,
                    isMazeBranch ? EdgeBranchRole.MazeEntry : EdgeBranchRole.None));
                if (isMazeBranch)
                {
                    string correctTunnelNodeId = AddQuizBranchCorrectAnswerTunnels(
                        nodes,
                        edges,
                        nodeId,
                        true);
                    AddQuizBranchWrongAnswerTunnels(nodes, edges, nodeId, optionCount);
                    prevNodeId = correctTunnelNodeId;
                    continue;
                }

                prevNodeId = nodeId;
            }

            switch (config.ExtractionMode)
            {
                case ExtractionMode.SeparateExitRoom:
                {
                    RoomNode exitNode = new("n_exit", RoomKind.Exit);
                    nodes.Add(exitNode);
                    edges.Add(new LevelGraphEdge(prevNodeId, exitNode.NodeId, true));
                    break;
                }
                case ExtractionMode.SameAsInsertion:
                {
                    edges.Add(new LevelGraphEdge(prevNodeId, startNode.NodeId, true));
                    break;
                }
                case ExtractionMode.InObjectiveRoom:
                {
                    break;
                }
                case ExtractionMode.DoubleExit:
                {
                    int count = Math.Max(2, config.ExtractionRoomsCount);
                    for (int e = 0; e < count; e++)
                    {
                        RoomNode exitNode = new($"n_exit_{e + 1}", RoomKind.Exit);
                        nodes.Add(exitNode);
                        edges.Add(new LevelGraphEdge(prevNodeId, exitNode.NodeId, true));
                    }

                    break;
                }
            }

            if (isScripted)
            {
                return BuildGraphWithDoorCounts(nodes, edges);
            }

            List<SpineEntry> ruleOptionalEntries = BuildRoomKindRuleEntries(config, nodes, rng);
            if (plannedBranchEntries.Count > 0)
            {
                ruleOptionalEntries.InsertRange(0, plannedBranchEntries);
            }

            int effectiveBranchPointBudget = branchPointBudget > 0 || ruleOptionalEntries.Count == 0
                ? branchPointBudget
                : 1;
            if (hasPlannedInteractiveContent)
            {
                int desiredBranchHostCount = DeterminePlannedBranchHostCount(plannedBranchEntries.Count);
                effectiveBranchPointBudget = Math.Max(
                    effectiveBranchPointBudget,
                    Math.Min(desiredBranchHostCount, Math.Max(1, nodes.Count - 2)));
            }
            else if (ruleOptionalEntries.Count > 0)
            {
                effectiveBranchPointBudget = Math.Max(
                    effectiveBranchPointBudget,
                    DesiredInteractiveBranchPoints(plannedInteractiveCount, nodes.Count));
            }

            targetTotalCount = effectiveBranchPointBudget == 0
                ? Math.Max(targetTotalCount, nodes.Count)
                : Math.Max(targetTotalCount, nodes.Count + branchRange.Min + deadEndRange.Min);
            targetTotalCount = Math.Max(targetTotalCount, nodes.Count + ruleOptionalEntries.Count);
            if (hasPlannedInteractiveContent)
            {
                int optionalFillerCap = Math.Min(4, Math.Max(0, effectiveBranchPointBudget * 2));
                int requiredPlannedOptionalRooms = nodes.Count + ruleOptionalEntries.Count;
                targetTotalCount = Math.Max(
                    requiredPlannedOptionalRooms,
                    Math.Min(targetTotalCount, requiredPlannedOptionalRooms + optionalFillerCap));
            }

            List<int> branchHostIndices = effectiveBranchPointBudget > 0
                ? PickBranchHostIndices(nodes, config, rng, effectiveBranchPointBudget)
                : new List<int>(0);

            int optionalBudget = Math.Max(0, targetTotalCount - nodes.Count);
            int branchChainCount = DetermineBranchChainCount(optionalBudget, effectiveBranchPointBudget, branchRange,
                branchHostIndices, rng);
            if (hasPlannedInteractiveContent && branchHostIndices.Count > 0)
            {
                int desiredInteractiveChains = Math.Min(
                    optionalBudget,
                    Math.Min(branchHostIndices.Count, Math.Max(1, plannedBranchEntries.Count)));
                branchChainCount = Math.Max(branchChainCount, desiredInteractiveChains);
            }

            int[] chainLengths = DistributeOptionalRooms(optionalBudget, branchChainCount, rng);
            int fillerBudget = Math.Max(0, optionalBudget - ruleOptionalEntries.Count);
            int requestedDeadEnds = Math.Min(fillerBudget, rng.NextInt(deadEndRange.Min, deadEndRange.Max + 1));
            int deadEndChains = Math.Min(requestedDeadEnds, branchChainCount);
            int branchIndex = 1;
            int deadEndIndex = 1;
            int fillerIndex = 1;
            int ruleEntryIndex = 0;
            int branchPassageSpacerBudget = hasPlannedInteractiveContent
                ? Math.Min(6, Math.Max(0, plannedBranchEntries.Count / 4))
                : 0;
            for (int chain = 0; chain < branchChainCount; chain++)
            {
                int hostIdx = branchHostIndices.Count > 0
                    ? branchHostIndices[chain % branchHostIndices.Count]
                    : PickBranchHostIndex(nodes, config, branchHostIndices, rng);

                if (nodes[hostIdx].Kind == RoomKind.Passage && string.IsNullOrEmpty(nodes[hostIdx].ContentId))
                {
                    nodes[hostIdx] = WithPreferredRoomTemplateId(nodes[hostIdx], "PlusTonnel_3Doors");
                }

                string previousNodeId = nodes[hostIdx].NodeId;
                int chainLength = chainLengths[chain];
                bool shouldTryInsertBranchPassageSpacer = branchPassageSpacerBudget > 0 && chain % 4 == 0;
                if (shouldTryInsertBranchPassageSpacer)
                {
                    chainLength++;
                }

                bool chainEndsInDeadEnd = chain < deadEndChains;
                for (int depth = 0; depth < chainLength; depth++)
                {
                    bool isLast = depth == chainLength - 1;
                    bool isDeadEnd = chainEndsInDeadEnd && isLast;
                    bool hasRuleEntry = ruleEntryIndex < ruleOptionalEntries.Count;
                    SpineEntry spec = hasRuleEntry
                        ? ruleOptionalEntries[ruleEntryIndex]
                        : new SpineEntry(RoomKind.Passage);
                    if (shouldTryInsertBranchPassageSpacer
                        && depth == 0
                        && hasRuleEntry
                        && spec.Kind == RoomKind.Interactive)
                    {
                        spec = new SpineEntry(
                            RoomKind.Passage,
                            "Toonel");
                        hasRuleEntry = false;
                        shouldTryInsertBranchPassageSpacer = false;
                        branchPassageSpacerBudget--;
                    }
                    else if (hasRuleEntry)
                    {
                        ruleEntryIndex++;
                    }

                    string nodeId = hasRuleEntry
                        ? $"n_rule_{RuleNodeName(spec.Kind)}_{ruleEntryIndex}"
                        : isDeadEnd
                            ? $"n_deadend_{deadEndIndex++}"
                            : depth == 0
                                ? $"n_branch_{branchIndex++}"
                                : $"n_optional_{fillerIndex++}";
                    RoomNode node = new(
                        nodeId,
                        spec.Kind,
                        isOnMainPath: false,
                        isDeadEnd: isDeadEnd,
                        preferredRoomTemplateId: spec.PreferredRoomTemplateId,
                        contentId: spec.ContentId);
                    nodes.Add(node);
                    bool isMazeBranch = TryResolveMazeBranchOptionCount(config, spec.ContentId, out int optionCount);
                    edges.Add(new LevelGraphEdge(
                        previousNodeId,
                        nodeId,
                        false,
                        isMazeBranch ? EdgeBranchRole.MazeEntry : EdgeBranchRole.None));
                    previousNodeId = nodeId;
                    if (isMazeBranch)
                    {
                        string correctTunnelNodeId = AddQuizBranchCorrectAnswerTunnels(
                            nodes,
                            edges,
                            nodeId,
                            false);
                        AddQuizBranchWrongAnswerTunnels(nodes, edges, nodeId, optionCount);
                        previousNodeId = correctTunnelNodeId;
                    }
                }

                if (config.AllowLoops
                    && chainLength > 0
                    && !chainEndsInDeadEnd
                    && TryPickReconnectTarget(nodes, hostIdx, rng, out string targetNodeId))
                {
                    edges.Add(new LevelGraphEdge(previousNodeId, targetNodeId, false));
                }
            }

            return BuildGraphWithDoorCounts(nodes, edges);
        }

        /// <summary>
        /// Граф принудительного пути: старт → перечисленные комнаты → одна выходная.
        /// <para>
        /// Содержимое комнаты едет вместе с ней (<see cref="RoomNode.ContentId"/>), иначе интерактивная
        /// комната осталась бы пустой и не показала бы урок. Раздувание «двери правильного ответа»
        /// намеренно НЕ выполняется: критический путь принудительного уровня — это ровно заказанные
        /// комнаты, и виду-двери (MazeBranch) остаётся жить по собственным правилам в обычной генерации.
        /// </para>
        /// </summary>
        private static bool TryBuildForcedGraph(
            IReadOnlyList<IForcedMainPathRoom> forcedRooms,
            out LevelGraph graph)
        {
            graph = null;
            if (forcedRooms == null || forcedRooms.Count == 0)
            {
                return false;
            }

            List<RoomNode> nodes = new(forcedRooms.Count + 2);
            List<LevelGraphEdge> edges = new(forcedRooms.Count + 1);
            RoomNode startNode = new("n_start", RoomKind.Start);
            nodes.Add(startNode);

            string previousNodeId = startNode.NodeId;
            for (int i = 0; i < forcedRooms.Count; i++)
            {
                IForcedMainPathRoom room = forcedRooms[i];
                if (room == null)
                {
                    return false;
                }

                string nodeId = $"n_forced_{i + 1}";
                nodes.Add(new RoomNode(
                    nodeId,
                    room.Kind,
                    isOnMainPath: true,
                    preferredRoomTemplateId: room.PreferredRoomTemplateId,
                    contentId: room.ContentId));
                edges.Add(new LevelGraphEdge(previousNodeId, nodeId, true));
                previousNodeId = nodeId;
            }

            RoomNode exitNode = new("n_exit", RoomKind.Exit);
            nodes.Add(exitNode);
            edges.Add(new LevelGraphEdge(previousNodeId, exitNode.NodeId, true));

            graph = BuildGraphWithDoorCounts(nodes, edges);
            return true;
        }

        private static RoomNode WithPreferredRoomTemplateId(RoomNode node, string preferredRoomTemplateId)
        {
            if (node == null || string.IsNullOrWhiteSpace(preferredRoomTemplateId))
            {
                return node;
            }

            if (!string.IsNullOrWhiteSpace(node.PreferredRoomTemplateId)
                && !(string.Equals(node.PreferredRoomTemplateId, "Toonel", StringComparison.Ordinal)
                     && string.Equals(preferredRoomTemplateId, "PlusTonnel_3Doors", StringComparison.Ordinal)))
            {
                return node;
            }

            return new RoomNode(
                node.NodeId,
                node.Kind,
                node.RequiredDoorCount,
                node.Tags,
                node.IsOnMainPath,
                node.IsDeadEnd,
                preferredRoomTemplateId,
                node.ContentId);
        }

        private static List<SpineEntry> BuildRoomKindRuleEntries(
            IMissionConfigView config,
            IReadOnlyList<RoomNode> existingNodes,
            IDeterministicRng rng)
        {
            List<SpineEntry> entries = new();
            AppendMissionRoomRuleEntries(config.MissionRoomRules, existingNodes, entries, rng);
            AppendRuleEntries(config.RoomKindSpawnRules, existingNodes, entries, rng);

            if (config.BonusRoomChance.IsSet)
            {
                AppendChanceEntry(RoomKind.Passage, config.BonusRoomChance.Value, entries, rng);
            }

            if (config.SecretRoomChance.IsSet)
            {
                AppendChanceEntry(RoomKind.Passage, config.SecretRoomChance.Value, entries, rng);
            }

            return entries;
        }

        private static void PreferStraightMainPassages(List<SpineEntry> spine)
        {
            if (spine == null)
            {
                return;
            }

            for (int i = 0; i < spine.Count; i++)
            {
                SpineEntry entry = spine[i];
                if (entry.Kind == RoomKind.Passage
                    && string.IsNullOrEmpty(entry.PreferredRoomTemplateId)
                    && string.IsNullOrEmpty(entry.ContentId))
                {
                    spine[i] = new SpineEntry(RoomKind.Passage, "Toonel");
                }
            }
        }

        private void PrependPlannedInteractiveEntries(
            IReadOnlyList<IInteractiveMissionContentSpec> content,
            List<SpineEntry> entries)
        {
            if (content == null || content.Count == 0)
            {
                return;
            }

            List<SpineEntry> merged = new(content.Count + entries.Count);
            for (int i = 0; i < content.Count; i++)
            {
                if (TryCreateInteractiveSpineEntry(content[i], out SpineEntry entry))
                {
                    merged.Add(entry);
                }
            }

            merged.AddRange(entries);
            entries.Clear();
            entries.AddRange(merged);
        }

        private static void ConvertUnassignedInteractiveToPassage(List<SpineEntry> spine)
        {
            if (spine == null)
            {
                return;
            }

            for (int i = 0; i < spine.Count; i++)
            {
                SpineEntry entry = spine[i];
                if (entry.Kind == RoomKind.Interactive
                    && string.IsNullOrEmpty(entry.PreferredRoomTemplateId)
                    && string.IsNullOrEmpty(entry.ContentId))
                {
                    spine[i] = new SpineEntry(RoomKind.Passage);
                }
            }
        }

        private static int DesiredInteractiveBranchPoints(int plannedInteractiveCount, int anchorCount)
        {
            if (plannedInteractiveCount <= 0)
            {
                return 0;
            }

            int desired = plannedInteractiveCount == 1
                ? 1
                : Math.Max(2, (plannedInteractiveCount + 9) / 10);
            int maxByAnchors = Math.Max(1, anchorCount - 2);
            return Clamp(desired, 1, Math.Min(6, maxByAnchors));
        }

        private List<SpineEntry> ApplyInteractiveContentToSpine(
            IMissionConfigView config,
            List<SpineEntry> spine,
            IDeterministicRng rng)
        {
            List<SpineEntry> branchEntries = new();
            IReadOnlyList<IInteractiveMissionContentSpec> content = ResolvePlannedInteractiveContent(config);
            if (content == null || content.Count == 0)
            {
                return branchEntries;
            }

            List<InteractiveSpineEntry> contentEntries = new(content.Count);
            for (int i = 0; i < content.Count; i++)
            {
                IInteractiveMissionContentSpec contentSpec = content[i];
                if (IsSupportedInteractiveContent(config, contentSpec)
                    && TryCreateInteractiveSpineEntry(contentSpec, out SpineEntry entry))
                {
                    string interactiveKind =
                        InteractiveMissionContentMatcher.NormalizeKind(contentSpec?.InteractiveKind);
                    InteractiveRoomGenerationHints hints =
                        InteractiveMissionContentMatcher.ResolveHints(_interactiveRoomKinds, contentSpec);
                    contentEntries.Add(new InteractiveSpineEntry(
                        entry,
                        RequiredPassageSpacerBefore(),
                        hints.PreferredSpacerRoomTemplateIdBefore,
                        interactiveKind,
                        contentSpec.IsMandatory,
                        hints));
                }
            }

            if (contentEntries.Count == 0)
            {
                return branchEntries;
            }

            contentEntries = ReorderInteractiveContentForPlacement(contentEntries, rng);
            int mandatoryContentCount = CountMandatoryInteractive(contentEntries);

            int mainInteractiveCount = Math.Min(
                contentEntries.Count,
                mandatoryContentCount > 0
                    ? mandatoryContentCount
                    : DetermineMainInteractiveCount(contentEntries.Count, spine.Count));
            List<InteractiveSpineEntry> mainContentEntries = SelectMainContentEntries(
                contentEntries,
                mainInteractiveCount,
                null,
                out List<InteractiveSpineEntry> branchContentEntries);
            MovePostponedContentToBack(mainContentEntries);

            BuildBranchInteractiveEntries(
                branchContentEntries,
                0,
                branchEntries,
                DetermineOptionalInteractiveBranchCount(branchContentEntries.Count, spine.Count));

            List<SpineEntry> baseEntries = new(spine.Count);
            for (int i = 0; i < spine.Count; i++)
            {
                SpineEntry entry = spine[i];
                bool isUnassignedInteractivePlaceholder = entry.Kind == RoomKind.Interactive
                                                          && string.IsNullOrEmpty(entry.PreferredRoomTemplateId)
                                                          && string.IsNullOrEmpty(entry.ContentId);
                if (!isUnassignedInteractivePlaceholder)
                {
                    baseEntries.Add(entry);
                }
            }

            if (baseEntries.Count == 0)
            {
                spine.Clear();
                string previousInteractiveKind = null;
                for (int i = 0; i < mainContentEntries.Count; i++)
                {
                    AppendInteractiveEntry(spine, mainContentEntries[i], ref previousInteractiveKind);
                }

                PreferPassagesAfterInteractiveContent(spine, config);
                return branchEntries;
            }

            List<SpineEntry> merged = new(baseEntries.Count + mainContentEntries.Count);
            int contentIndex = 0;
            string previousInteractiveKindInMerged = null;
            for (int i = 0; i < baseEntries.Count; i++)
            {
                SpineEntry baseEntry = baseEntries[i];
                if (contentIndex < mainContentEntries.Count
                    && ShouldInsertInteractiveAtBaseIndex(
                        i,
                        baseEntries.Count,
                        contentIndex,
                        mainContentEntries.Count))
                {
                    if (!string.IsNullOrEmpty(mainContentEntries[contentIndex].PreferredPassageSpacerRoomTemplateId)
                        && baseEntry.Kind == RoomKind.Passage
                        && string.IsNullOrEmpty(baseEntry.PreferredRoomTemplateId)
                        && string.IsNullOrEmpty(baseEntry.ContentId))
                    {
                        baseEntry = new SpineEntry(
                            RoomKind.Passage,
                            mainContentEntries[contentIndex].PreferredPassageSpacerRoomTemplateId);
                    }

                    merged.Add(baseEntry);
                    previousInteractiveKindInMerged = null;
                    int extraSpacers = mainContentEntries[contentIndex].PassageSpacerBefore - 1;
                    AddPassageSpacers(
                        merged,
                        extraSpacers,
                        mainContentEntries[contentIndex].PreferredPassageSpacerRoomTemplateId);
                    if (extraSpacers > 0)
                    {
                        previousInteractiveKindInMerged = null;
                    }

                    AppendInteractiveEntry(merged, mainContentEntries[contentIndex],
                        ref previousInteractiveKindInMerged);
                    contentIndex++;
                }
                else
                {
                    merged.Add(baseEntry);
                    if (baseEntry.Kind == RoomKind.Passage)
                    {
                        previousInteractiveKindInMerged = null;
                    }
                }
            }

            while (contentIndex < mainContentEntries.Count)
            {
                AppendInteractiveEntry(merged, mainContentEntries[contentIndex], ref previousInteractiveKindInMerged);
                contentIndex++;
            }

            spine.Clear();
            PreferPassagesAfterInteractiveContent(merged, config);
            spine.AddRange(merged);
            return branchEntries;
        }

        private static int DetermineMainInteractiveCount(int contentCount, int baseSpineCount)
        {
            if (contentCount <= 4)
            {
                return contentCount;
            }

            return Math.Min(4, contentCount);
        }

        private static int DetermineRequiredMainInteractiveCount(int contentCount, int requiredKindCount)
        {
            if (requiredKindCount <= 0)
            {
                return 0;
            }

            return requiredKindCount;
        }

        private static List<InteractiveSpineEntry> SelectMainContentEntries(
            IReadOnlyList<InteractiveSpineEntry> entries,
            int mainInteractiveCount,
            string requiredNormalizedKind,
            out List<InteractiveSpineEntry> branchEntries)
        {
            branchEntries = new List<InteractiveSpineEntry>();
            if (entries == null || entries.Count == 0 || mainInteractiveCount <= 0)
            {
                return new List<InteractiveSpineEntry>();
            }

            bool[] selected = new bool[entries.Count];
            int selectedCount = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsMandatory
                    || (!string.IsNullOrEmpty(requiredNormalizedKind)
                        && string.Equals(entries[i].InteractiveKind, requiredNormalizedKind, StringComparison.Ordinal)))
                {
                    selected[i] = true;
                    selectedCount++;
                }
            }

            for (int i = 0; i < entries.Count && selectedCount < mainInteractiveCount; i++)
            {
                if (selected[i])
                {
                    continue;
                }

                selected[i] = true;
                selectedCount++;
            }

            List<InteractiveSpineEntry> mainEntries = new(Math.Min(mainInteractiveCount, entries.Count));
            for (int i = 0; i < entries.Count; i++)
            {
                if (selected[i])
                {
                    mainEntries.Add(entries[i]);
                }
                else
                {
                    branchEntries.Add(entries[i]);
                }
            }

            return mainEntries;
        }

        private static int CountInteractiveKind(
            IReadOnlyList<InteractiveSpineEntry> entries,
            string normalizedKind)
        {
            if (entries == null || string.IsNullOrEmpty(normalizedKind))
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].InteractiveKind, normalizedKind, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountMandatoryInteractive(IReadOnlyList<InteractiveSpineEntry> entries)
        {
            if (entries == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsMandatory)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountPlannedInteractivePlacements(
            IMissionConfigView config,
            IReadOnlyList<IInteractiveMissionContentSpec> content)
        {
            if (content == null || content.Count == 0)
            {
                return 0;
            }

            int guaranteed = 0;
            int optional = 0;
            for (int i = 0; i < content.Count; i++)
            {
                IInteractiveMissionContentSpec item = content[i];
                if (!IsSupportedInteractiveContent(config, item))
                {
                    continue;
                }

                if (item.IsMandatory)
                {
                    guaranteed++;
                }
                else
                {
                    optional++;
                }
            }

            return guaranteed + DetermineOptionalInteractiveBranchCount(optional, 0);
        }

        private static bool ShouldInsertInteractiveAtBaseIndex(
            int baseIndex,
            int baseCount,
            int contentIndex,
            int contentCount)
        {
            if (contentCount <= 0)
            {
                return false;
            }

            if (baseCount <= 0)
            {
                return true;
            }

            int targetIndex = (contentIndex + 1) * baseCount / (contentCount + 1);
            return baseIndex >= targetIndex;
        }

        private bool IsSupportedInteractiveContent(
            IMissionConfigView config,
            IInteractiveMissionContentSpec content)
        {
            if (content == null)
            {
                return false;
            }

            // An unknown kind is not filtered here: MissionGenerator.CreateInteractiveContentConfig drops it
            // with a warning before the graph is built.
            return !InteractiveMissionContentMatcher.TryResolveContentRule(
                       _interactiveRoomKinds, content, out IInteractiveRoomKindRule rule)
                   || rule.SupportsContent(content);
        }

        private static void MoveInteractiveKindToFront(
            List<InteractiveSpineEntry> entries,
            string normalizedKind)
        {
            if (entries == null || entries.Count <= 1 || string.IsNullOrEmpty(normalizedKind))
            {
                return;
            }

            List<InteractiveSpineEntry> prioritized = new(entries.Count);
            List<InteractiveSpineEntry> remaining = new(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                InteractiveSpineEntry entry = entries[i];
                if (string.Equals(entry.InteractiveKind, normalizedKind, StringComparison.Ordinal))
                {
                    prioritized.Add(entry);
                }
                else
                {
                    remaining.Add(entry);
                }
            }

            if (prioritized.Count == 0)
            {
                return;
            }

            entries.Clear();
            entries.AddRange(prioritized);
            entries.AddRange(remaining);
        }

        private static void MovePostponedContentToBack(List<InteractiveSpineEntry> entries)
        {
            if (entries == null || entries.Count <= 1)
            {
                return;
            }

            List<InteractiveSpineEntry> remaining = new(entries.Count);
            List<InteractiveSpineEntry> postponed = new(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                InteractiveSpineEntry entry = entries[i];
                if (entry.Hints.PlaceLastAmongMainPathContent)
                {
                    postponed.Add(entry);
                }
                else
                {
                    remaining.Add(entry);
                }
            }

            if (postponed.Count == 0)
            {
                return;
            }

            entries.Clear();
            entries.AddRange(remaining);
            entries.AddRange(postponed);
        }

        private void PreferPassagesAfterInteractiveContent(List<SpineEntry> spine, IMissionConfigView config)
        {
            if (spine == null || spine.Count < 2 || config == null)
            {
                return;
            }

            for (int i = 0; i < spine.Count - 1; i++)
            {
                SpineEntry current = spine[i];
                SpineEntry next = spine[i + 1];
                if (string.IsNullOrWhiteSpace(current.ContentId)
                    || next.Kind != RoomKind.Passage
                    || (!string.IsNullOrEmpty(next.PreferredRoomTemplateId)
                        && !string.Equals(next.PreferredRoomTemplateId, "Toonel", StringComparison.Ordinal))
                    || !string.IsNullOrEmpty(next.ContentId))
                {
                    continue;
                }

                if (InteractiveMissionContentMatcher.TryFindContentSpec(
                        config,
                        current.ContentId,
                        out IInteractiveMissionContentSpec content))
                {
                    string preferredRoomTemplateId = InteractiveMissionContentMatcher
                        .ResolveHints(_interactiveRoomKinds, content)
                        .PreferredPassageRoomTemplateIdAfter;
                    if (!string.IsNullOrEmpty(preferredRoomTemplateId))
                    {
                        spine[i + 1] = new SpineEntry(RoomKind.Passage, preferredRoomTemplateId);
                    }
                }
            }
        }

        private static int DeterminePlannedBranchHostCount(int plannedBranchEntryCount)
        {
            if (plannedBranchEntryCount <= 0)
            {
                return 0;
            }

            return Clamp(plannedBranchEntryCount, 4, 24);
        }

        private static string AddQuizBranchCorrectAnswerTunnels(
            List<RoomNode> nodes,
            List<LevelGraphEdge> edges,
            string quizNodeId,
            bool isOnMainPath)
        {
            string firstTunnelNodeId = $"{quizNodeId}_answer_correct";
            string secondTunnelNodeId = $"{quizNodeId}_answer_correct_extra";
            nodes.Add(new RoomNode(
                firstTunnelNodeId,
                RoomKind.Passage,
                isOnMainPath: isOnMainPath,
                preferredRoomTemplateId: "Toonel"));
            nodes.Add(new RoomNode(
                secondTunnelNodeId,
                RoomKind.Passage,
                isOnMainPath: isOnMainPath,
                preferredRoomTemplateId: "Toonel"));
            edges.Add(new LevelGraphEdge(quizNodeId, firstTunnelNodeId, isOnMainPath,
                EdgeBranchRole.MazeCorrectExit));
            edges.Add(new LevelGraphEdge(firstTunnelNodeId, secondTunnelNodeId, isOnMainPath));
            return secondTunnelNodeId;
        }

        private static void AddQuizBranchWrongAnswerTunnels(
            List<RoomNode> nodes,
            List<LevelGraphEdge> edges,
            string quizNodeId,
            int optionCount)
        {
            if (nodes == null || edges == null || string.IsNullOrWhiteSpace(quizNodeId))
            {
                return;
            }

            int wrongAnswerCount = Math.Min(3, Math.Max(0, optionCount - 1));
            for (int option = 0; option < wrongAnswerCount; option++)
            {
                string nodeId = $"{quizNodeId}_wrong_{option + 1}";
                nodes.Add(new RoomNode(
                    nodeId,
                    RoomKind.Passage,
                    isOnMainPath: false,
                    isDeadEnd: false,
                    preferredRoomTemplateId: "Toonel"));
                edges.Add(new LevelGraphEdge(quizNodeId, nodeId, false, EdgeBranchRole.MazeWrongDeadEnd));
            }
        }

        private bool TryResolveMazeBranchOptionCount(
            IMissionConfigView config,
            string contentId,
            out int optionCount)
        {
            optionCount = 0;
            return config != null
                   && !string.IsNullOrWhiteSpace(contentId)
                   && InteractiveMissionContentMatcher.TryFindContentSpec(
                       config,
                       contentId,
                       out IInteractiveMissionContentSpec content)
                   && InteractiveMissionContentMatcher.TryResolveDoorBranchOptionCount(
                       _interactiveRoomKinds,
                       content,
                       out optionCount);
        }

        private static void BuildBranchInteractiveEntries(
            IReadOnlyList<InteractiveSpineEntry> contentEntries,
            int startIndex,
            List<SpineEntry> branchEntries,
            int maxCount = int.MaxValue)
        {
            if (contentEntries == null || branchEntries == null || maxCount <= 0)
            {
                return;
            }

            string previousInteractiveKind = null;
            int added = 0;
            for (int i = Math.Max(0, startIndex); i < contentEntries.Count && added < maxCount; i++)
            {
                InteractiveSpineEntry content = contentEntries[i];
                AppendInteractiveEntry(branchEntries, content, ref previousInteractiveKind);
                added++;
                if (!string.IsNullOrEmpty(content.Hints.BranchPassageRoomTemplateIdAfter))
                {
                    branchEntries.Add(new SpineEntry(RoomKind.Passage, content.Hints.BranchPassageRoomTemplateIdAfter));
                }

                previousInteractiveKind = null;
            }
        }

        private static int DetermineOptionalInteractiveBranchCount(int optionalContentCount, int spineCount)
        {
            if (optionalContentCount <= 0)
            {
                return 0;
            }

            return Math.Min(optionalContentCount,
                Math.Min(1, DesiredInteractiveBranchPoints(optionalContentCount, spineCount)));
        }

        private static int RequiredPassageSpacerBefore() => 1;

        private static void AddPassageSpacers(List<SpineEntry> entries, int count, string preferredRoomTemplateId)
        {
            for (int i = 0; i < count; i++)
            {
                entries.Add(new SpineEntry(RoomKind.Passage, preferredRoomTemplateId));
            }
        }

        private static void AppendInteractiveEntry(
            List<SpineEntry> entries,
            InteractiveSpineEntry content,
            ref string previousInteractiveKind)
        {
            if (RequiresPassageBetweenInteractiveRooms(previousInteractiveKind, content.InteractiveKind))
            {
                AddPassageSpacers(
                    entries,
                    content.PassageSpacerBefore,
                    content.PreferredPassageSpacerRoomTemplateId ?? "Toonel");
                previousInteractiveKind = null;
            }

            entries.Add(content.Entry);
            previousInteractiveKind = content.InteractiveKind;
        }

        private static bool RequiresPassageBetweenInteractiveRooms(string previousKind, string currentKind) => !string.IsNullOrEmpty(previousKind) && !string.IsNullOrEmpty(currentKind);

        private static List<InteractiveSpineEntry> ReorderInteractiveContentForPlacement(
            List<InteractiveSpineEntry> entries,
            IDeterministicRng rng)
        {
            if (entries == null || entries.Count <= 2)
            {
                return entries;
            }

            List<string> kinds = new();
            List<Queue<InteractiveSpineEntry>> groups = new();
            for (int i = 0; i < entries.Count; i++)
            {
                InteractiveSpineEntry entry = entries[i];
                int groupIndex = IndexOfKind(kinds, entry.InteractiveKind);
                if (groupIndex < 0)
                {
                    kinds.Add(entry.InteractiveKind);
                    groups.Add(new Queue<InteractiveSpineEntry>());
                    groupIndex = groups.Count - 1;
                }

                groups[groupIndex].Enqueue(entry);
            }

            for (int i = 0; i < groups.Count; i++)
            {
                ShuffleQueue(groups[i], rng);
            }

            List<InteractiveSpineEntry> result = new(entries.Count);
            string previousKind = null;
            while (result.Count < entries.Count)
            {
                int bestIndex = PickLargestGroup(groups, kinds, previousKind, rng);
                if (bestIndex < 0)
                {
                    break;
                }

                InteractiveSpineEntry next = groups[bestIndex].Dequeue();
                result.Add(next);
                previousKind = next.InteractiveKind;
            }

            return result.Count == entries.Count ? result : entries;
        }

        private static void ShuffleQueue(Queue<InteractiveSpineEntry> queue, IDeterministicRng rng)
        {
            if (queue == null || queue.Count <= 1 || rng == null)
            {
                return;
            }

            List<InteractiveSpineEntry> items = new(queue);
            for (int i = items.Count - 1; i > 0; i--)
            {
                int swapIndex = rng.NextInt(0, i + 1);
                (items[i], items[swapIndex]) = (items[swapIndex], items[i]);
            }

            queue.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                queue.Enqueue(items[i]);
            }
        }

        private static int IndexOfKind(IReadOnlyList<string> kinds, string kind)
        {
            for (int i = 0; i < kinds.Count; i++)
            {
                if (string.Equals(kinds[i], kind, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int PickLargestGroup(
            IReadOnlyList<Queue<InteractiveSpineEntry>> groups,
            IReadOnlyList<string> kinds,
            string previousKind,
            IDeterministicRng rng)
        {
            int bestIndex = -1;
            int bestCount = -1;
            int matchingBestCount = 0;
            bool hasAlternative = HasNonPreviousGroup(groups, kinds, previousKind);
            for (int i = 0; i < groups.Count; i++)
            {
                int count = groups[i].Count;
                if (count <= 0)
                {
                    continue;
                }

                if (hasAlternative && string.Equals(kinds[i], previousKind, StringComparison.Ordinal))
                {
                    continue;
                }

                if (count > bestCount)
                {
                    bestCount = count;
                    bestIndex = i;
                    matchingBestCount = 1;
                }
                else if (count == bestCount && rng != null)
                {
                    matchingBestCount++;
                    if (rng.NextInt(0, matchingBestCount) == 0)
                    {
                        bestIndex = i;
                    }
                }
            }

            return bestIndex;
        }

        private static bool HasNonPreviousGroup(
            IReadOnlyList<Queue<InteractiveSpineEntry>> groups,
            IReadOnlyList<string> kinds,
            string previousKind)
        {
            if (string.IsNullOrEmpty(previousKind))
            {
                return true;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i].Count > 0 && !string.Equals(kinds[i], previousKind, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<IInteractiveMissionContentSpec> ResolvePlannedInteractiveContent(
            IMissionConfigView config)
        {
            if (config is IPlannedInteractiveMissionContentProvider planned
                && planned.PlannedInteractiveContent != null)
            {
                return planned.PlannedInteractiveContent;
            }

            if (config is IInteractiveMissionContentProvider raw && raw.InteractiveContent != null)
            {
                return raw.InteractiveContent;
            }

            return Array.Empty<IInteractiveMissionContentSpec>();
        }

        private bool TryCreateInteractiveSpineEntry(
            IInteractiveMissionContentSpec content,
            out SpineEntry entry)
        {
            if (content == null || string.IsNullOrWhiteSpace(content.ContentId))
            {
                entry = default;
                return false;
            }

            string preferredRoomTemplateId = InteractiveMissionContentMatcher
                .ResolveHints(_interactiveRoomKinds, content)
                .PreferredRoomTemplateId;
            entry = new SpineEntry(RoomKind.Interactive, preferredRoomTemplateId, content.ContentId);
            return true;
        }

        private static void AppendMissionRoomRuleEntries(
            IReadOnlyList<IMissionRoomRule> rules,
            IReadOnlyList<RoomNode> existingNodes,
            List<SpineEntry> entries,
            IDeterministicRng rng)
        {
            if (rules == null)
            {
                return;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                IMissionRoomRule rule = rules[i];
                if (rule == null || !rule.HasSpawnRule || string.IsNullOrEmpty(rule.RoomId) ||
                    !CanSpawnByRule(rule.Kind))
                {
                    continue;
                }

                if (rule.Mode == RoomTypeSpawnMode.Chance)
                {
                    AppendChanceEntry(rule, ResolveMissionRoomRuleChance(rules, rule), entries, rng);
                    continue;
                }

                int min = Math.Max(0, rule.CountRange.Min);
                int max = Math.Max(min, rule.CountRange.Max);
                int targetCount = rng.NextInt(min, max + 1);
                int currentCount = CountPreferredRoom(existingNodes, rule.RoomId) +
                                   CountPreferredRoom(entries, rule.RoomId);
                int missing = Math.Max(0, targetCount - currentCount);
                for (int n = 0; n < missing; n++)
                {
                    entries.Add(new SpineEntry(rule.Kind, rule.RoomId));
                }
            }
        }

        private static void AppendRuleEntries(
            IReadOnlyList<IRoomKindSpawnRule> rules,
            IReadOnlyList<RoomNode> existingNodes,
            List<SpineEntry> entries,
            IDeterministicRng rng)
        {
            if (rules == null)
            {
                return;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                IRoomKindSpawnRule rule = rules[i];
                if (rule == null || !CanSpawnByRule(rule.Kind))
                {
                    continue;
                }

                if (rule.Mode == RoomTypeSpawnMode.Chance)
                {
                    AppendChanceEntry(rule.Kind, ResolveRuleChance(rules, rule), entries, rng);
                    continue;
                }

                int min = Math.Max(0, rule.CountRange.Min);
                int max = Math.Max(min, rule.CountRange.Max);
                int targetCount = rng.NextInt(min, max + 1);
                int currentCount = CountKind(existingNodes, rule.Kind) + CountKind(entries, rule.Kind);
                int missing = Math.Max(0, targetCount - currentCount);
                for (int n = 0; n < missing; n++)
                {
                    entries.Add(new SpineEntry(rule.Kind));
                }
            }
        }

        private static void AppendChanceEntry(RoomKind kind, float chance, List<SpineEntry> entries,
            IDeterministicRng rng)
        {
            if (!CanSpawnByRule(kind))
            {
                return;
            }

            float clampedChance = chance < 0f ? 0f : chance > 1f ? 1f : chance;
            if (clampedChance <= 0f || rng.NextFloat() >= clampedChance)
            {
                return;
            }

            entries.Add(new SpineEntry(kind));
        }

        private static void AppendChanceEntry(IMissionRoomRule rule, float chance, List<SpineEntry> entries,
            IDeterministicRng rng)
        {
            float clampedChance = chance < 0f ? 0f : chance > 1f ? 1f : chance;
            if (clampedChance <= 0f || rng.NextFloat() >= clampedChance)
            {
                return;
            }

            entries.Add(new SpineEntry(rule.Kind, rule.RoomId));
        }

        private static float ResolveRuleChance(IReadOnlyList<IRoomKindSpawnRule> rules, IRoomKindSpawnRule current)
        {
            if (current.OverrideChance)
            {
                return current.Chance;
            }

            if (rules == null || rules.Count == 0)
            {
                return current.Chance;
            }

            int autoRuleCount = 0;
            float overriddenChanceSum = 0f;
            for (int i = 0; i < rules.Count; i++)
            {
                IRoomKindSpawnRule rule = rules[i];
                if (rule == null || rule.Mode != RoomTypeSpawnMode.Chance)
                {
                    continue;
                }

                if (rule.OverrideChance)
                {
                    overriddenChanceSum += Clamp01(rule.Chance);
                }
                else
                {
                    autoRuleCount++;
                }
            }

            if (autoRuleCount <= 0)
            {
                return current.Chance;
            }

            float remainingChance = 1f - overriddenChanceSum;
            if (remainingChance <= 0f)
            {
                return 0f;
            }

            return remainingChance / autoRuleCount;
        }

        private static float ResolveMissionRoomRuleChance(IReadOnlyList<IMissionRoomRule> rules,
            IMissionRoomRule current)
        {
            if (current.OverrideChance)
            {
                return current.Chance;
            }

            if (rules == null || rules.Count == 0)
            {
                return current.Chance;
            }

            int autoRuleCount = 0;
            float overriddenChanceSum = 0f;
            for (int i = 0; i < rules.Count; i++)
            {
                IMissionRoomRule rule = rules[i];
                if (rule == null || !rule.HasSpawnRule || rule.Mode != RoomTypeSpawnMode.Chance)
                {
                    continue;
                }

                if (rule.OverrideChance)
                {
                    overriddenChanceSum += Clamp01(rule.Chance);
                }
                else
                {
                    autoRuleCount++;
                }
            }

            if (autoRuleCount <= 0)
            {
                return current.Chance;
            }

            float remainingChance = 1f - overriddenChanceSum;
            if (remainingChance <= 0f)
            {
                return 0f;
            }

            return remainingChance / autoRuleCount;
        }

        private static bool CanSpawnByRule(RoomKind kind) => kind != RoomKind.Start && kind != RoomKind.Exit;

        private static int CountKind(IReadOnlyList<RoomNode> nodes, RoomKind kind)
        {
            int count = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountPreferredRoom(IReadOnlyList<RoomNode> nodes, string roomId)
        {
            if (nodes == null || string.IsNullOrEmpty(roomId))
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (string.Equals(nodes[i].PreferredRoomTemplateId, roomId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountPreferredRoom(IReadOnlyList<SpineEntry> entries, string roomId)
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].PreferredRoomTemplateId, roomId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountKind(IReadOnlyList<SpineEntry> entries, RoomKind kind)
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private static string RuleNodeName(RoomKind kind) => kind.ToString().ToLowerInvariant();

        private static int DetermineBranchChainCount(
            int optionalBudget,
            int branchPointBudget,
            IntRange branchRange,
            IReadOnlyList<int> branchHostIndices,
            IDeterministicRng rng)
        {
            if (optionalBudget <= 0
                || branchPointBudget <= 0
                || (branchHostIndices != null && branchHostIndices.Count == 0))
            {
                return 0;
            }

            int requested = Math.Max(1, rng.NextInt(branchRange.Min, branchRange.Max + 1));
            int hostCount = branchHostIndices != null && branchHostIndices.Count > 0
                ? branchHostIndices.Count
                : branchPointBudget;
            int maxChains = Math.Min(optionalBudget, Math.Max(1, hostCount * 2));
            return Clamp(requested, 1, maxChains);
        }

        private static int[] DistributeOptionalRooms(int optionalBudget, int chainCount, IDeterministicRng rng)
        {
            if (optionalBudget <= 0 || chainCount <= 0)
            {
                return Array.Empty<int>();
            }

            int[] result = new int[chainCount];
            int baseLength = Math.Max(1, optionalBudget / chainCount);
            for (int i = 0; i < chainCount; i++)
            {
                result[i] = baseLength;
            }

            int remaining = optionalBudget - baseLength * chainCount;
            List<int> availableIndices = new(chainCount);
            for (int i = 0; i < chainCount; i++)
            {
                availableIndices.Add(i);
            }

            while (remaining > 0)
            {
                int availableIndex = rng.NextInt(0, availableIndices.Count);
                int chainIndex = availableIndices[availableIndex];
                availableIndices.RemoveAt(availableIndex);
                result[chainIndex]++;
                remaining--;
            }

            return result;
        }

        private static IntRange ResolveRange(OptionalIntRange optional, IntRange tilesetDefault) => optional.IsSet ? new IntRange(optional.Min, optional.Max) : tilesetDefault;

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

        private static int CountExitNodes(IMissionConfigView config)
        {
            return config.ExtractionMode switch
            {
                ExtractionMode.SeparateExitRoom => 1,
                ExtractionMode.DoubleExit => Math.Max(2, config.ExtractionRoomsCount),
                _ => 0
            };
        }

        private static int PickBranchHostIndex(
            IReadOnlyList<RoomNode> nodes,
            IMissionConfigView config,
            IReadOnlyList<int> branchHostIndices,
            IDeterministicRng rng)
        {
            if (branchHostIndices != null && branchHostIndices.Count > 0)
            {
                return branchHostIndices[rng.NextInt(0, branchHostIndices.Count)];
            }

            return PickBranchHostIndices(nodes, config, rng, 1)[0];
        }

        private static List<int> PickBranchHostIndices(
            IReadOnlyList<RoomNode> nodes,
            IMissionConfigView config,
            IDeterministicRng rng,
            int maxCount)
        {
            List<int> candidates = new();
            for (int i = 1; i < Math.Max(1, nodes.Count - 1); i++)
            {
                RoomNode node = nodes[i];
                if (!node.IsOnMainPath)
                {
                    continue;
                }

                if (node.Kind == RoomKind.Exit || node.Kind == RoomKind.Start)
                {
                    continue;
                }

                if (!CanHostSideBranch(node, config))
                {
                    continue;
                }

                candidates.Add(i);
            }

            if (candidates.Count == 0 && nodes.Count > 1)
            {
                candidates.Add(Math.Min(1, nodes.Count - 1));
            }

            List<int> result = new(Math.Min(maxCount, candidates.Count));
            int count = Math.Min(Math.Max(1, maxCount), candidates.Count);
            if (count > 1 && candidates.Count > count)
            {
                int stride = Math.Max(1, (candidates.Count + count - 1) / count);
                int offset = stride > 1 ? rng.NextInt(0, stride) : 0;
                int guard = 0;
                while (result.Count < count && guard++ < candidates.Count * 2)
                {
                    int index = (offset + result.Count * stride) % candidates.Count;
                    int candidate = candidates[index];
                    if (!result.Contains(candidate))
                    {
                        result.Add(candidate);
                    }
                    else
                    {
                        offset++;
                    }
                }

                if (result.Count == count)
                {
                    return result;
                }

                candidates.RemoveAll(result.Contains);
            }

            while (result.Count < count)
            {
                int candidateIndex = rng.NextInt(0, candidates.Count);
                result.Add(candidates[candidateIndex]);
                candidates.RemoveAt(candidateIndex);
            }

            return result;
        }

        private static bool CanHostSideBranch(RoomNode node, IMissionConfigView config)
        {
            if (node == null)
            {
                return false;
            }

            if (node.Kind == RoomKind.Passage && string.IsNullOrEmpty(node.ContentId))
            {
                return true;
            }

            return false;
        }

        private static bool TryPickReconnectTarget(
            IReadOnlyList<RoomNode> nodes,
            int hostIdx,
            IDeterministicRng rng,
            out string targetNodeId)
        {
            List<string> candidates = new();
            for (int i = hostIdx + 1; i < nodes.Count; i++)
            {
                RoomNode node = nodes[i];
                if (!node.IsOnMainPath)
                {
                    continue;
                }

                if (node.Kind == RoomKind.Start)
                {
                    continue;
                }

                candidates.Add(node.NodeId);
            }

            if (candidates.Count == 0)
            {
                targetNodeId = null;
                return false;
            }

            targetNodeId = candidates[rng.NextInt(0, candidates.Count)];
            return true;
        }

        private static bool ShouldUseHybridSequence(IMissionConfigView config)
        {
            return config.GenerationMode == LevelGenerationMode.Hybrid
                   && config.HybridSequence != null
                   && config.HybridSequence.Count > 0;
        }

        private static List<SpineEntry> BuildScriptedSpine(IReadOnlyList<IHybridSequenceStep> sequence)
        {
            List<SpineEntry> spine = new(sequence != null ? sequence.Count : 0);
            if (sequence == null)
            {
                return spine;
            }

            for (int i = 0; i < sequence.Count; i++)
            {
                IHybridSequenceStep step = sequence[i];
                if (step == null)
                {
                    continue;
                }

                switch (step.Kind)
                {
                    case HybridSequenceSlotKind.RoomPrefab:
                        if (!string.IsNullOrEmpty(step.RoomId) && CanSpawnByRule(step.RoomKind))
                        {
                            spine.Add(new SpineEntry(step.RoomKind, step.RoomId));
                        }

                        break;

                    case HybridSequenceSlotKind.RoomKind:
                        if (CanSpawnByRule(step.RoomKind))
                        {
                            spine.Add(new SpineEntry(step.RoomKind));
                        }

                        break;
                }
            }

            return spine;
        }

        private static List<SpineEntry> BuildHybridSpine(
            IReadOnlyList<IHybridSequenceStep> sequence,
            IReadOnlyList<RoomKind> required,
            int targetSpineCount,
            IDeterministicRng rng)
        {
            List<SpineEntry> spine = new(Math.Max(targetSpineCount, 4));
            bool hasAnchors = false;

            for (int i = 0; i < sequence.Count; i++)
            {
                IHybridSequenceStep step = sequence[i];
                if (step == null)
                {
                    continue;
                }

                switch (step.Kind)
                {
                    case HybridSequenceSlotKind.Other:
                    {
                        OptionalIntRange range = step.OtherCountRange.IsSet
                            ? step.OtherCountRange
                            : new OptionalIntRange(1, 1);
                        int min = Math.Max(0, range.Min);
                        int max = Math.Max(min, range.Max);
                        int count = rng.NextInt(min, max + 1);
                        for (int c = 0; c < count; c++)
                        {
                            spine.Add(BuildFillerEntry(spine.Count));
                        }

                        break;
                    }

                    case HybridSequenceSlotKind.RoomPrefab:
                        if (!string.IsNullOrEmpty(step.RoomId) && CanSpawnByRule(step.RoomKind))
                        {
                            spine.Add(new SpineEntry(step.RoomKind, step.RoomId));
                            hasAnchors = true;
                        }

                        break;

                    case HybridSequenceSlotKind.RoomKind:
                        if (CanSpawnByRule(step.RoomKind))
                        {
                            spine.Add(new SpineEntry(step.RoomKind));
                            hasAnchors = true;
                        }

                        break;
                }
            }

            if (!hasAnchors && spine.Count == 0)
            {
                spine.Add(new SpineEntry(RoomKind.Passage));
                spine.Add(new SpineEntry(RoomKind.Passage));
                spine.Add(new SpineEntry(RoomKind.Interactive));
            }

            AppendMissingRequiredKinds(spine, required);

            while (spine.Count < targetSpineCount)
            {
                int insertIndex = Math.Max(1, spine.Count - 1);
                spine.Insert(insertIndex, BuildFillerEntry(spine.Count));
            }

            return spine;
        }

        private static List<SpineEntry> BuildSpine(MissionArchetypeKind archetype, IReadOnlyList<RoomKind> required,
            int targetSpineCount)
        {
            List<SpineEntry> spine = new(Math.Max(targetSpineCount, 4));
            spine.Add(new SpineEntry(RoomKind.Passage));

            switch (archetype)
            {
                case MissionArchetypeKind.Simple:
                    spine.Add(new SpineEntry(RoomKind.Passage));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    break;

                case MissionArchetypeKind.Collection:
                    spine.Add(new SpineEntry(RoomKind.Passage));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    break;

                case MissionArchetypeKind.WithSideBranches:
                    spine.Add(new SpineEntry(RoomKind.Passage));
                    spine.Add(new SpineEntry(RoomKind.Passage));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    break;

                case MissionArchetypeKind.Final:
                    spine.Add(new SpineEntry(RoomKind.Passage));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    spine.Add(new SpineEntry(RoomKind.Interactive));
                    spine.Add(new SpineEntry(RoomKind.Passage));
                    break;
            }

            AppendMissingRequiredKinds(spine, required);

            while (spine.Count < targetSpineCount)
            {
                int insertIndex = Math.Max(1, spine.Count - 1);
                spine.Insert(insertIndex, BuildFillerEntry(spine.Count));
            }

            return spine;
        }

        private static void AppendMissingRequiredKinds(List<SpineEntry> spine, IReadOnlyList<RoomKind> required)
        {
            if (required == null)
            {
                return;
            }

            for (int i = 0; i < required.Count; i++)
            {
                RoomKind kind = required[i];
                if (!CanSpawnByRule(kind) || ContainsKind(spine, kind))
                {
                    continue;
                }

                spine.Insert(Math.Max(0, spine.Count - 1), new SpineEntry(kind));
            }
        }

        private static SpineEntry BuildFillerEntry(int index) => new(RoomKind.Passage);

        private static bool ContainsKind(List<SpineEntry> spine, RoomKind kind)
        {
            for (int i = 0; i < spine.Count; i++)
            {
                if (spine[i].Kind == kind)
                {
                    return true;
                }
            }

            return false;
        }

        private static LevelGraph BuildGraphWithDoorCounts(List<RoomNode> nodes, List<LevelGraphEdge> edges)
        {
            Dictionary<string, int> degreeByNode = new(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Count; i++)
            {
                degreeByNode[nodes[i].NodeId] = 0;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                LevelGraphEdge edge = edges[i];
                if (degreeByNode.ContainsKey(edge.FromNodeId))
                {
                    degreeByNode[edge.FromNodeId]++;
                }

                if (degreeByNode.ContainsKey(edge.ToNodeId))
                {
                    degreeByNode[edge.ToNodeId]++;
                }
            }

            List<RoomNode> resultNodes = new(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                RoomNode node = nodes[i];
                int requiredDoors = degreeByNode.TryGetValue(node.NodeId, out int degree) ? degree : 0;
                resultNodes.Add(node.WithRequiredDoorCount(requiredDoors));
            }

            return new LevelGraph(resultNodes, edges);
        }

        private readonly struct SpineEntry
        {
            public readonly RoomKind Kind;
            public readonly string PreferredRoomTemplateId;
            public readonly string ContentId;

            public SpineEntry(RoomKind kind, string preferredRoomTemplateId = null, string contentId = null)
            {
                Kind = kind;
                PreferredRoomTemplateId = preferredRoomTemplateId;
                ContentId = contentId;
            }
        }

        private readonly struct InteractiveSpineEntry
        {
            public readonly SpineEntry Entry;
            public readonly int PassageSpacerBefore;
            public readonly string PreferredPassageSpacerRoomTemplateId;
            public readonly string InteractiveKind;
            public readonly bool IsMandatory;
            public readonly InteractiveRoomGenerationHints Hints;

            public InteractiveSpineEntry(
                SpineEntry entry,
                int passageSpacerBefore,
                string preferredPassageSpacerRoomTemplateId,
                string interactiveKind,
                bool isMandatory,
                InteractiveRoomGenerationHints hints)
            {
                Entry = entry;
                PassageSpacerBefore = Math.Max(1, passageSpacerBefore);
                PreferredPassageSpacerRoomTemplateId = preferredPassageSpacerRoomTemplateId;
                InteractiveKind = interactiveKind;
                IsMandatory = isMandatory;
                Hints = hints ?? InteractiveRoomGenerationHints.None;
            }
        }
    }
}
