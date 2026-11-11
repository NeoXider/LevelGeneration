using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedoSchool.LevelGeneration.Runtime.Application
{
    /// <summary>
    /// Пол под точкой ленты: луч вниз с высоты над последней принятой точкой. Возвращает, нашёл ли он пол,
    /// и высоту попадания. Реализация — адаптер физики у вызывающего; в тестах — функция.
    /// </summary>
    public delegate bool RouteHintFloorProbe(Vector2 point, float fromFloorHeight, out float hitHeight);

    /// <summary>
    /// Какую высоту принять для очередной точки ленты. ПОДЪЁМ — только в пределах <c>maxStepMeters</c> от
    /// ПРЕДЫДУЩЕЙ принятой точки (лестница и пандус ступенька за ступенькой), иначе высота предыдущей точки
    /// (ящик, стол, декор — лента остаётся на полу). СПУСК принимается всегда: луч бьёт в ближайшую поверхность
    /// под собой, и поверхность ниже предыдущей точки — это пол, на который сошли (со стола, с ящика, со
    /// ступени). Раньше спуск больше шага тоже отвергался, и лента, начатая на столе под учеником, так и
    /// висела на высоте стола через всю комнату, перелезая через стулья.
    /// </summary>
    public static class ResolveRouteHintFloorHeightUseCase
    {
        public static float Execute(bool hit, float hitHeight, float previousHeight, float maxStepMeters)
        {
            if (!hit || float.IsNaN(hitHeight) || float.IsInfinity(hitHeight))
            {
                return previousHeight;
            }

            if (hitHeight <= previousHeight)
            {
                return hitHeight;
            }

            return hitHeight - previousHeight <= Math.Max(0f, maxStepMeters) ? hitHeight : previousHeight;
        }
    }

    /// <summary>Форма ленты подсказки на полу; значения приходят из ScriptableObject подсказки.</summary>
    public readonly struct RouteHintRibbonShape
    {
        public readonly float WidthMeters;

        /// <summary>Наибольший радиус скругления угла пути.</summary>
        public readonly float CornerRadiusMeters;

        /// <summary>
        /// Насколько скруглённая лента может отойти от точки поворота. Точки пути — двери и обходы стен,
        /// поэтому скругление обязано проходить рядом с ними, а не срезать угол через стену.
        /// </summary>
        public readonly float CornerMaxDeviationMeters;

        /// <summary>Шаг разбиения прямых участков (и, значит, точек луча к полу).</summary>
        public readonly float SampleSpacingMeters;

        /// <summary>На сколько лента приподнята над полом — против мерцания с полом.</summary>
        public readonly float LiftMeters;

        /// <summary>Наибольший перепад пола между соседними точками ленты.</summary>
        public readonly float MaxStepMeters;

        public RouteHintRibbonShape(float widthMeters, float cornerRadiusMeters, float cornerMaxDeviationMeters,
            float sampleSpacingMeters, float liftMeters, float maxStepMeters = 0.35f)
        {
            WidthMeters = Mathf.Max(0.01f, widthMeters);
            CornerRadiusMeters = Mathf.Max(0f, cornerRadiusMeters);
            CornerMaxDeviationMeters = Mathf.Max(0f, cornerMaxDeviationMeters);
            SampleSpacingMeters = Mathf.Max(0.02f, sampleSpacingMeters);
            LiftMeters = liftMeters;
            MaxStepMeters = Mathf.Max(0f, maxStepMeters);
        }
    }

    /// <summary>
    /// Готовые массивы меша ленты (мировые координаты) и осевая линия для звёздочек. Списки переиспользуются
    /// между построениями: лента перестраивается при каждом новом показе подсказки.
    /// </summary>
    public sealed class RouteHintRibbonMeshData
    {
        /// <summary>Пара вершин на точку оси: левая (UV.x = 0) и правая (UV.x = 1).</summary>
        public readonly List<Vector3> Vertices = new();

        /// <summary>x — поперёк ленты 0…1, y — расстояние от игрока вдоль ленты, метры.</summary>
        public readonly List<Vector2> Uv0 = new();

        /// <summary>x — доля пути от игрока к цели 0…1, y — полная длина ленты, метры.</summary>
        public readonly List<Vector2> Uv1 = new();

        public readonly List<int> Triangles = new();

        /// <summary>Осевая линия ленты в мировых координатах (с высотой пола и подъёмом).</summary>
        public readonly List<Vector3> Centerline = new();

        /// <summary>Расстояние каждой точки оси от начала ленты, метры.</summary>
        public readonly List<float> CenterlineDistances = new();

        public float LengthMeters { get; internal set; }

        public void Clear()
        {
            Vertices.Clear();
            Uv0.Clear();
            Uv1.Clear();
            Triangles.Clear();
            Centerline.Clear();
            CenterlineDistances.Clear();
            LengthMeters = 0f;
        }
    }

    /// <summary>Почему лента не построена.</summary>
    public enum RouteHintRibbonFailure
    {
        None = 0,

        /// <summary>Меньше двух конечных различимых точек или путь без длины.</summary>
        Degenerate = 1,

        /// <summary>Даже минимальные скругления дают больше <see cref="RouteHintRibbonMeshBuilder.HardMaxSamples"/> точек.</summary>
        TooComplex = 2
    }

    /// <summary>
    /// Строит ленту подсказки маршрута на полу из той же полилинии, что рисует миникарта
    /// (<see cref="PlanRouteHintPathUseCase"/>, мировые XZ): скругляет углы, не отходя от точек поворота
    /// дальше <see cref="RouteHintRibbonShape.CornerMaxDeviationMeters"/>, разбивает прямые участки, ставит
    /// каждую точку на пол (<see cref="RouteHintFloorProbe"/> + <see cref="ResolveRouteHintFloorHeightUseCase"/>)
    /// и собирает полосу из пар вершин. Своего планировщика у ленты нет. Чистый C#: тестируется без сцены.
    /// </summary>
    public static class RouteHintRibbonMeshBuilder
    {
        /// <summary>
        /// Потолок точек оси: длинный путь не превращается в тяжёлый меш. Мягкий: точки углов (вход, вершина и
        /// выход скругления, острый угол) сохраняются всегда, даже если их одних больше потолка.
        /// </summary>
        public const int MaxSamples = 512;

        /// <summary>
        /// Жёсткий потолок точек оси. Если даже минимальные скругления его превышают (тысячи изломов —
        /// патологический путь), ленты нет, и пунктир на миникарте показывается сразу. Вершин не больше
        /// 4·<see cref="HardMaxSamples"/> (пара на точку, ещё пара на шов) — заведомо ниже 16-битного индекса меша.
        /// </summary>
        public const int HardMaxSamples = MaxSamples * 4;

        private const float DuplicatePointMeters = 0.001f;
        private const int MaxCornerSubdivisions = 12;

        /// <summary>
        /// Поворот почти назад (больше ~164°, sin половины угла поворота больше 0.99): скругление сложило бы
        /// ленту, поэтому угол остаётся острым, и в нём всегда ставится шов из двух пар вершин. Один порог на
        /// оба решения — иначе между ними оставались бы острые углы без шва, где лента сужается.
        /// </summary>
        private const float ReversalTurnSine = 0.99f;

        public static bool Build(
            IReadOnlyList<Vector2> polyline,
            RouteHintRibbonShape shape,
            float startFloorHeight,
            RouteHintFloorProbe probe,
            RouteHintRibbonMeshData output) =>
            Build(polyline, shape, startFloorHeight, probe, output, out _);

        public static bool Build(
            IReadOnlyList<Vector2> polyline,
            RouteHintRibbonShape shape,
            float startFloorHeight,
            RouteHintFloorProbe probe,
            RouteHintRibbonMeshData output,
            out RouteHintRibbonFailure failure)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            output.Clear();
            List<Vector2> axis = new();
            failure = SmoothCorners(polyline, shape, axis);
            if (failure != RouteHintRibbonFailure.None)
            {
                return false;
            }

            return BuildFromAxis(axis, shape, startFloorHeight, probe, output, out failure);
        }

        /// <summary>
        /// Same as <see cref="Build(IReadOnlyList{Vector2}, RouteHintRibbonShape, float, RouteHintFloorProbe, RouteHintRibbonMeshData, out RouteHintRibbonFailure)"/>
        /// for an axis that is already smoothed: the points are used exactly as given (no rounding, no
        /// resampling), only the floor height is probed for each. The route hint passes the shared trail here, so
        /// the floor view and the minimap draw the very same points.
        /// </summary>
        public static bool BuildFromAxis(
            IReadOnlyList<Vector2> axis,
            RouteHintRibbonShape shape,
            float startFloorHeight,
            RouteHintFloorProbe probe,
            RouteHintRibbonMeshData output,
            out RouteHintRibbonFailure failure) =>
            BuildAxisCore(axis, shape, startFloorHeight, probe, null, null, output, out failure);

        /// <summary>
        /// Same as <see cref="BuildFromAxis(IReadOnlyList{Vector2}, RouteHintRibbonShape, float, RouteHintFloorProbe, RouteHintRibbonMeshData, out RouteHintRibbonFailure)"/>
        /// and also returns the accepted floor height under every axis point (<paramref name="floorHeights"/>, one per
        /// point, without the lift). The route hint probes once per re-plan and keeps these heights in the trail.
        /// </summary>
        public static bool BuildFromAxis(
            IReadOnlyList<Vector2> axis,
            RouteHintRibbonShape shape,
            float startFloorHeight,
            RouteHintFloorProbe probe,
            RouteHintRibbonMeshData output,
            out RouteHintRibbonFailure failure,
            List<float> floorHeights) =>
            BuildAxisCore(axis, shape, startFloorHeight, probe, null, floorHeights, output, out failure);

        /// <summary>
        /// The ribbon for an already smoothed axis whose floor heights are known (one per point, without the lift):
        /// no probes at all. A blend of the trail moves the points only in XZ, and the heights probed for the plan
        /// stay valid, so the floor follows the morph without a raycast per point per frame. With the heights that
        /// <see cref="BuildFromAxis(IReadOnlyList{Vector2}, RouteHintRibbonShape, float, RouteHintFloorProbe, RouteHintRibbonMeshData, out RouteHintRibbonFailure, List{float})"/>
        /// returned for the same points, the result is the same ribbon.
        /// </summary>
        public static bool BuildFromAxisAtHeights(
            IReadOnlyList<Vector2> axis,
            IReadOnlyList<float> floorHeights,
            RouteHintRibbonShape shape,
            RouteHintRibbonMeshData output,
            out RouteHintRibbonFailure failure)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (axis == null || floorHeights == null || floorHeights.Count < axis.Count || floorHeights.Count == 0)
            {
                output.Clear();
                failure = RouteHintRibbonFailure.Degenerate;
                return false;
            }

            return BuildAxisCore(axis, shape, floorHeights[0], null, floorHeights, null, output, out failure);
        }

        /// <param name="knownHeights">Floor height per point; when set, <paramref name="probe"/> is not used.</param>
        /// <param name="heightsOut">Receives the accepted floor height per point, if set.</param>
        private static bool BuildAxisCore(
            IReadOnlyList<Vector2> axis,
            RouteHintRibbonShape shape,
            float startFloorHeight,
            RouteHintFloorProbe probe,
            IReadOnlyList<float> knownHeights,
            List<float> heightsOut,
            RouteHintRibbonMeshData output,
            out RouteHintRibbonFailure failure)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            output.Clear();
            heightsOut?.Clear();
            failure = RouteHintRibbonFailure.None;
            if (axis == null || axis.Count < 2 || !IsFinite(startFloorHeight))
            {
                failure = RouteHintRibbonFailure.Degenerate;
                return false;
            }

            if (axis.Count > HardMaxSamples)
            {
                failure = RouteHintRibbonFailure.TooComplex;
                return false;
            }

            // Длина считается первым проходом, расстояние до точки — накоплением во втором, в том же порядке
            // сложений: без списка расстояний на каждое построение (лента строится и на кадрах смешивания).
            float total = 0f;
            for (int i = 1; i < axis.Count; i++)
            {
                total += Vector2.Distance(axis[i - 1], axis[i]);
            }

            if (total <= DuplicatePointMeters || !IsFinite(total))
            {
                failure = RouteHintRibbonFailure.Degenerate;
                return false;
            }

            float halfWidth = shape.WidthMeters * 0.5f;
            float floor = startFloorHeight;
            float distance = 0f;
            for (int i = 0; i < axis.Count; i++)
            {
                Vector2 point = axis[i];
                if (i > 0)
                {
                    distance += Vector2.Distance(axis[i - 1], point);
                }

                if (knownHeights != null)
                {
                    floor = knownHeights[i];
                }
                else
                {
                    bool hit = false;
                    float hitHeight = floor;
                    if (probe != null)
                    {
                        hit = probe(point, floor, out hitHeight);
                    }

                    floor = ResolveRouteHintFloorHeightUseCase.Execute(hit, hitHeight, floor, shape.MaxStepMeters);
                }

                heightsOut?.Add(floor);
                Vector2 incoming = i > 0 ? (point - axis[i - 1]).normalized : Vector2.zero;
                Vector2 outgoing = i < axis.Count - 1 ? (axis[i + 1] - point).normalized : Vector2.zero;
                if (IsReversal(incoming, outgoing))
                {
                    // Разворот назад: шов из двух пар вершин в одной точке — к ней по входящему направлению,
                    // от неё по исходящему. Пролёт между ними нулевой длины, и лента не перекручивается.
                    AddPair(output, point, incoming, floor + shape.LiftMeters, halfWidth, distance, total);
                    AddPair(output, point, outgoing, floor + shape.LiftMeters, halfWidth, distance, total);
                    continue;
                }

                AddPair(output, point, Tangent(incoming, outgoing), floor + shape.LiftMeters, halfWidth, distance,
                    total);
            }

            int pairs = output.Centerline.Count;
            for (int i = 0; i + 1 < pairs; i++)
            {
                int leftA = i * 2;
                int rightA = leftA + 1;
                int leftB = leftA + 2;
                int rightB = leftA + 3;
                output.Triangles.Add(leftA);
                output.Triangles.Add(leftB);
                output.Triangles.Add(rightA);
                output.Triangles.Add(rightA);
                output.Triangles.Add(leftB);
                output.Triangles.Add(rightB);
            }

            output.LengthMeters = total;
            return true;
        }

        /// <summary>
        /// Осевая линия ленты: углы скруглены квадратичной кривой Безье с вершиной в точке поворота, прямые
        /// участки разбиты с шагом <see cref="RouteHintRibbonShape.SampleSpacingMeters"/>. Радиус каждого
        /// угла ограничен половиной соседних отрезков и допуском отхода от точки поворота; разворот почти назад
        /// не скругляется. Не больше <see cref="MaxSamples"/> точек: сначала укрупняется шаг ПРЯМЫХ участков
        /// (их точки всё равно лежат на отрезках пути), потом, если не хватило, у скруглений остаются вход,
        /// вершина и выход. Точки углов не выбрасываются никогда: лента всегда проходит у дверей и обходов стен
        /// не дальше допуска и не срезает поворот хордой через стену. Если и минимальные скругления больше
        /// <see cref="HardMaxSamples"/>, ось не строится (<see cref="RouteHintRibbonFailure.TooComplex"/>).
        /// </summary>
        public static RouteHintRibbonFailure SmoothCorners(IReadOnlyList<Vector2> polyline,
            RouteHintRibbonShape shape, List<Vector2> output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            output.Clear();
            List<Vector2> points = CleanPoints(polyline);
            if (points.Count < 2)
            {
                return RouteHintRibbonFailure.Degenerate;
            }

            List<AxisPiece> pieces = BuildPieces(points, shape);
            float spacing = shape.SampleSpacingMeters;
            bool minimalCurves = false;
            if (CountSamples(pieces, spacing, false) > MaxSamples)
            {
                minimalCurves = CountSamples(pieces, float.MaxValue, false) > MaxSamples;
                long fewest = CountSamples(pieces, float.MaxValue, minimalCurves);
                if (fewest > HardMaxSamples)
                {
                    return RouteHintRibbonFailure.TooComplex;
                }

                spacing = fewest > MaxSamples ? float.MaxValue : FindStraightSpacing(pieces, spacing, minimalCurves);
            }

            output.Add(points[0]);
            for (int i = 0; i < pieces.Count; i++)
            {
                AxisPiece piece = pieces[i];
                if (piece.IsCurve)
                {
                    AppendCorner(output, piece.From, piece.Corner, piece.To,
                        minimalCurves ? piece.MinimalSteps : piece.NominalSteps);
                }
                else
                {
                    AppendStraight(output, piece.From, piece.To, spacing);
                }
            }

            return RouteHintRibbonFailure.None;
        }

        /// <summary>Прямые участки и скругления оси в порядке пути; острый угол — конец прямого участка.</summary>
        private static List<AxisPiece> BuildPieces(List<Vector2> points, RouteHintRibbonShape shape)
        {
            List<AxisPiece> pieces = new();
            Vector2 cursor = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                Vector2 corner = points[i];
                bool last = i == points.Count - 1;
                float radius = last ? 0f : CornerRadius(points[i - 1], corner, points[i + 1], shape);
                Vector2 incoming = (corner - points[i - 1]).normalized;
                Vector2 entry = corner - incoming * radius;
                bool rounded = !last && radius > DuplicatePointMeters;
                Vector2 straightEnd = rounded ? entry : corner;
                if ((straightEnd - cursor).sqrMagnitude >= DuplicatePointMeters * DuplicatePointMeters)
                {
                    pieces.Add(AxisPiece.Straight(cursor, straightEnd));
                }

                if (!rounded)
                {
                    cursor = corner;
                    continue;
                }

                Vector2 outgoing = (points[i + 1] - corner).normalized;
                Vector2 exit = corner + outgoing * radius;
                pieces.Add(AxisPiece.Curve(entry, corner, exit, shape.SampleSpacingMeters, incoming, outgoing));
                cursor = exit;
            }

            return pieces;
        }

        private static long CountSamples(List<AxisPiece> pieces, float straightSpacing, bool minimalCurves)
        {
            long count = 1;
            for (int i = 0; i < pieces.Count; i++)
            {
                AxisPiece piece = pieces[i];
                count += piece.IsCurve
                    ? minimalCurves ? piece.MinimalSteps : piece.NominalSteps
                    : StraightSteps(piece.Length, straightSpacing);
            }

            return count;
        }

        /// <summary>Самый мелкий шаг прямых участков, при котором ось укладывается в <see cref="MaxSamples"/>.</summary>
        private static float FindStraightSpacing(List<AxisPiece> pieces, float nominal, bool minimalCurves)
        {
            float low = nominal;
            float high = nominal;
            for (int i = 0; i < pieces.Count; i++)
            {
                high = Mathf.Max(high, pieces[i].Length);
            }

            high *= 2f;
            for (int iteration = 0; iteration < 40; iteration++)
            {
                float middle = (low + high) * 0.5f;
                if (CountSamples(pieces, middle, minimalCurves) > MaxSamples)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return high;
        }

        /// <summary>
        /// Число шагов прямого участка, не больше <c>MaxSamples + 1</c>: больше одного участка всё равно не
        /// уложится в бюджет, а участок в сотни миллионов метров иначе дал бы переполнение и миллиарды точек.
        /// </summary>
        private static int StraightSteps(float length, float spacing)
        {
            if (spacing >= float.MaxValue)
            {
                return 1;
            }

            float steps = length / spacing;
            return steps <= MaxSamples + 1 ? Mathf.Max(1, Mathf.CeilToInt(steps)) : MaxSamples + 1;
        }

        private static bool IsReversal(Vector2 incoming, Vector2 outgoing) =>
            incoming.sqrMagnitude > 0.5f && outgoing.sqrMagnitude > 0.5f
                                         && (incoming - outgoing).magnitude * 0.5f > ReversalTurnSine;

        private static float CornerRadius(Vector2 previous, Vector2 corner, Vector2 next, RouteHintRibbonShape shape)
        {
            Vector2 incoming = corner - previous;
            Vector2 outgoing = next - corner;
            float inLength = incoming.magnitude;
            float outLength = outgoing.magnitude;
            if (inLength <= DuplicatePointMeters || outLength <= DuplicatePointMeters)
            {
                return 0f;
            }

            // Квадратичная кривая с вершиной в углу отходит от него на 0.5·r·sin(θ/2), θ — угол поворота.
            if (IsReversal(incoming / inLength, outgoing / outLength))
            {
                return 0f;
            }

            float turnSine = (incoming / inLength - outgoing / outLength).magnitude * 0.5f;

            float radius = Mathf.Min(shape.CornerRadiusMeters, inLength * 0.5f, outLength * 0.5f);
            if (turnSine > 0.0001f)
            {
                radius = Mathf.Min(radius, shape.CornerMaxDeviationMeters / (0.5f * turnSine));
            }

            return Mathf.Max(0f, radius);
        }

        private static void AppendStraight(List<Vector2> output, Vector2 from, Vector2 to, float spacing)
        {
            int steps = StraightSteps(Vector2.Distance(from, to), spacing);
            for (int step = 1; step <= steps; step++)
            {
                AppendIfFar(output, step == steps ? to : Vector2.Lerp(from, to, step / (float)steps));
            }
        }

        private static void AppendCorner(List<Vector2> output, Vector2 entry, Vector2 corner, Vector2 exit, int steps)
        {
            for (int step = 1; step <= steps; step++)
            {
                float t = step / (float)steps;
                float u = 1f - t;
                AppendIfFar(output, u * u * entry + 2f * u * t * corner + t * t * exit);
            }
        }

        private static void AppendIfFar(List<Vector2> output, Vector2 point)
        {
            if (output.Count > 0 && (output[output.Count - 1] - point).sqrMagnitude
                < DuplicatePointMeters * DuplicatePointMeters)
            {
                return;
            }

            output.Add(point);
        }

        /// <summary>Точки без NaN/бесконечностей и без повторов подряд.</summary>
        private static List<Vector2> CleanPoints(IReadOnlyList<Vector2> polyline)
        {
            List<Vector2> points = new();
            if (polyline == null)
            {
                return points;
            }

            for (int i = 0; i < polyline.Count; i++)
            {
                Vector2 point = polyline[i];
                if (IsFinite(point.x) && IsFinite(point.y))
                {
                    AppendIfFar(points, point);
                }
            }

            return points;
        }

        private static void AddPair(RouteHintRibbonMeshData output, Vector2 point, Vector2 tangent, float y,
            float halfWidth, float distance, float total)
        {
            Vector2 left = new(-tangent.y, tangent.x);
            Vector2 leftPoint = point + left * halfWidth;
            Vector2 rightPoint = point - left * halfWidth;
            output.Vertices.Add(new Vector3(leftPoint.x, y, leftPoint.y));
            output.Vertices.Add(new Vector3(rightPoint.x, y, rightPoint.y));
            output.Uv0.Add(new Vector2(0f, distance));
            output.Uv0.Add(new Vector2(1f, distance));
            Vector2 progress = new(distance / total, total);
            output.Uv1.Add(progress);
            output.Uv1.Add(progress);
            output.Centerline.Add(new Vector3(point.x, y, point.y));
            output.CenterlineDistances.Add(distance);
        }

        /// <summary>
        /// Направление оси в точке: сумма единичных входящего и исходящего направлений. Если сумма
        /// вырождается, берётся входящее направление, а не случайное.
        /// </summary>
        private static Vector2 Tangent(Vector2 incoming, Vector2 outgoing)
        {
            Vector2 sum = incoming + outgoing;
            if (sum.sqrMagnitude > 0.04f)
            {
                return sum.normalized;
            }

            if (incoming.sqrMagnitude > 0.5f)
            {
                return incoming;
            }

            return outgoing.sqrMagnitude > 0.5f ? outgoing : Vector2.right;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Кусок оси: прямой участок или скругление угла с числом разбиений.</summary>
        private readonly struct AxisPiece
        {
            public readonly bool IsCurve;
            public readonly Vector2 From;
            public readonly Vector2 Corner;
            public readonly Vector2 To;
            public readonly float Length;

            /// <summary>Разбиение скругления при обычном шаге; всегда чётное — вершина кривой попадает в ось.</summary>
            public readonly int NominalSteps;

            /// <summary>Минимум при нехватке бюджета: вход, вершина, выход (и две промежуточные на крутом угле).</summary>
            public readonly int MinimalSteps;

            private AxisPiece(bool isCurve, Vector2 from, Vector2 corner, Vector2 to, float length, int nominalSteps,
                int minimalSteps)
            {
                IsCurve = isCurve;
                From = from;
                Corner = corner;
                To = to;
                Length = length;
                NominalSteps = nominalSteps;
                MinimalSteps = minimalSteps;
            }

            public static AxisPiece Straight(Vector2 from, Vector2 to) =>
                new(false, from, to, to, Vector2.Distance(from, to), 0, 0);

            public static AxisPiece Curve(Vector2 entry, Vector2 corner, Vector2 exit, float spacing,
                Vector2 incoming, Vector2 outgoing)
            {
                float approximateLength = Vector2.Distance(entry, corner) + Vector2.Distance(corner, exit);
                int nominal = Mathf.Clamp(Mathf.CeilToInt(approximateLength / (spacing * 0.5f)), 2,
                    MaxCornerSubdivisions);
                nominal += nominal % 2;
                int minimal = Vector2.Dot(incoming, outgoing) < 0f ? 4 : 2;
                return new AxisPiece(true, entry, corner, exit, approximateLength, Mathf.Max(nominal, minimal),
                    minimal);
            }
        }
    }
}
