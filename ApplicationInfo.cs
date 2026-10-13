using System.Diagnostics;
using System.Reflection;

namespace Basip
{
    /// <summary>
    /// Сведения о запущенном приложении. Версия определяется в одном месте,
    /// чтобы значение в журнале и в работе службы не расходилось.
    /// </summary>
    public static class ApplicationInfo
    {
        private static string? cachedVersion;

        /// <summary>
        /// Версия приложения: AssemblyVersion из Basip.csproj, а если она не задана —
        /// версия самого exe-файла.
        /// </summary>
        public static string Version
        {
            get
            {
                // Версия не меняется за время работы процесса: определение кэшируется,
                // чтобы не читать файл повторно.
                cachedVersion ??= DetectVersion();
                return cachedVersion;
            }
        }

        /// <summary>Полный путь к исполняемому файлу (для однофайловой сборки — путь к exe).</summary>
        public static string? ExecutablePath => Environment.ProcessPath;

        private static string DetectVersion()
        {
            try
            {
                // AssemblyVersion — основной источник (задаётся в Basip.csproj)
                string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
                if (!string.IsNullOrEmpty(version) && version != "0.0.0.0")
                {
                    return version;
                }

                // Резерв: версия из самого exe-файла.
                // Assembly.Location в однофайловой сборке всегда пуст (IL3000), поэтому
                // путь берём через Environment.ProcessPath — иначе эта ветка не работала.
                string? exePath = ExecutablePath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    FileVersionInfo fileInfo = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrEmpty(fileInfo.ProductVersion))
                    {
                        return fileInfo.ProductVersion;
                    }

                    if (!string.IsNullOrEmpty(fileInfo.FileVersion))
                    {
                        return fileInfo.FileVersion;
                    }
                }
            }
            catch (Exception)
            {
                // Версию определить не удалось — ниже возвращается значение по умолчанию.
            }

            return "не определена";
        }
    }
}
