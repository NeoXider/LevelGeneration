namespace Neoxider.LevelGeneration.Generation.Application.Pipeline
{
    /// <summary>
    /// Room templates the generator inserts by itself, named by the host's tileset: a plain passage (spacers and
    /// the answer tunnels of a maze branch), the hub a branch chain starts from, and the dead end of a wrong answer.
    /// The generator knows the roles, never the ids — those belong to the host's room set. A role left empty
    /// (<see cref="None"/>) means «any compatible template».
    /// <para>
    /// Answer tunnels and caps of a maze branch must share the door family of the maze room's answer sockets,
    /// otherwise the connector cannot pair them and validation fails with «answer door not connected»; a tileset
    /// with such a constraint names its templates here.
    /// </para>
    /// </summary>
    public sealed class RoomTemplateRoles
    {
        /// <summary>No preferred templates: selection picks any compatible room.</summary>
        public static readonly RoomTemplateRoles None = new(null, null, null);

        public RoomTemplateRoles(string passageTemplateId, string branchHubTemplateId, string answerDeadEndTemplateId)
        {
            PassageTemplateId = Normalize(passageTemplateId);
            BranchHubTemplateId = Normalize(branchHubTemplateId);
            AnswerDeadEndTemplateId = Normalize(answerDeadEndTemplateId);
        }

        /// <summary>Plain passage: spacers between interactive rooms and the answer tunnels of a maze branch.</summary>
        public string PassageTemplateId { get; }

        /// <summary>Hub a branch chain starts from (a passage with a door for the branch).</summary>
        public string BranchHubTemplateId { get; }

        /// <summary>Dead end that terminates a wrong answer of a maze branch.</summary>
        public string AnswerDeadEndTemplateId { get; }

        private static string Normalize(string id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }
}
