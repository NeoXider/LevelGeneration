using System.Runtime.CompilerServices;
using UnityEngine;

namespace Neoxider.LevelGeneration.Runtime.Infrastructure
{
    /// <summary>
    /// Журнал рантайма карты уровня. Игра подключает свой журнал через <see cref="InfoSink"/> и
    /// <see cref="WarnSink"/>: приёмник получает исходный файл и метод вызова, поэтому журнал игры строит
    /// ту же подпись, что и при прямом вызове. Без приёмника записи идут в консоль Unity.
    /// </summary>
    public static class LevelGenerationRuntimeLog
    {
        public delegate void Sink(string message, string file, string member);

        public static Sink InfoSink { get; set; }

        public static Sink WarnSink { get; set; }

        public static void Info(
            string message,
            [CallerFilePath] string file = "",
            [CallerMemberName] string member = "")
        {
            if (InfoSink != null)
            {
                InfoSink(message, file, member);
                return;
            }

            UnityEngine.Debug.Log(Format(file, member, message));
        }

        public static void Warn(
            string message,
            [CallerFilePath] string file = "",
            [CallerMemberName] string member = "")
        {
            if (WarnSink != null)
            {
                WarnSink(message, file, member);
                return;
            }

            UnityEngine.Debug.LogWarning(Format(file, member, message));
        }

        private static string Format(string file, string member, string message) =>
            $"[LevelGeneration.{FileStem(file)}.{member}] {message}";

        /// <summary>
        /// Имя файла без расширения. Не <c>Path.GetFileNameWithoutExtension</c>: путь зашит компилятором на машине
        /// сборки, и в WebGL обратная косая — не разделитель, поэтому в консоль браузера ушёл бы весь путь.
        /// </summary>
        private static string FileStem(string file)
        {
            string path = file ?? string.Empty;
            int start = path.LastIndexOfAny(new[] { '/', '\\', ':' }) + 1;
            int end = path.LastIndexOf('.');
            return end > start ? path.Substring(start, end - start) : path.Substring(start);
        }

        /// <summary>
        /// Приёмники прошлой игровой сессии сбрасываются при следующем входе в Play Mode (domain reload может быть
        /// выключен); игра назначает их заново до загрузки первой сцены.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnSubsystemRegistration()
        {
            InfoSink = null;
            WarnSink = null;
        }
    }
}
