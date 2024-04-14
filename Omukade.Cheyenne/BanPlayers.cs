using Spectre.Console;

namespace Omukade.Cheyenne
{
    internal static class BanPlayers
    {
        static readonly List<string> PlayerNames = new();

        static FileSystemWatcher? watcher = null;

        public static event EventHandler? OnUpdate;

        public static bool IsBan(string? name)
        {
            return !string.IsNullOrEmpty(name) && PlayerNames.Contains(name, StringComparer.InvariantCultureIgnoreCase);
        }

        public static void Initialization()
        {
            if (watcher != null)
            {
                return;
            }
            string path = Program.config.BanPlayersFile;
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, Array.Empty<byte>());
            }
            FileInfo file = new FileInfo(path);
            if (file.Directory == null)
            {
                AnsiConsole.WriteLine("Unable to obtain the directory where ban players file is located.");
                return;
            }
            watcher = new FileSystemWatcher(file.Directory.FullName)
            {
                Filter = file.Name
            };
            watcher.Changed += Watcher_Changed;
            watcher.EnableRaisingEvents = true;
            Update(file.FullName);
        }

        static void Update(string path)
        {
            PlayerNames.Clear();
            string[] lines = Array.Empty<string>();
            try
            {
                lines = File.ReadAllText(path).Split('\n');
            }
            catch (Exception e)
            {
                AnsiConsole.WriteException(e);
            }
            foreach (string line in lines)
            {
                string name = line.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                };
                PlayerNames.Add(name);
            }

            if (OnUpdate != null)
            {
                OnUpdate(null, new EventArgs());
            }
        }

        static void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            Task.Run(() =>
            {
                Task.Delay(1000).Wait();
                Update(e.FullPath);
            });
        }
    }
}