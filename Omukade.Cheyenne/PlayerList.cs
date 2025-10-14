using Spectre.Console;
using System.IO;

namespace Omukade.Cheyenne
{
    internal class PlayerList: IDisposable
    {
        public event EventHandler? OnUpdate;

        private readonly string file = "";

        private readonly List<string> players = new();

        private FileSystemWatcher? watcher = null;

        public PlayerList(string file)
        {
            this.file = file;
            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, Array.Empty<byte>());
            }
            FileInfo fileInfo = new FileInfo(file);
            if (fileInfo.Directory == null)
            {
                AnsiConsole.WriteLine("Unable to obtain the directory where ban players file is located.");
                return;
            }
            watcher = new FileSystemWatcher(fileInfo.Directory.FullName)
            {
                Filter = fileInfo.Name
            };
            watcher.Changed += Watcher_Changed;
            watcher.EnableRaisingEvents = true;
            Update();
        }

        public bool Contains(string? name)
        {
            return !string.IsNullOrEmpty(name) && players.Contains(name, StringComparer.InvariantCultureIgnoreCase);
        }

        public bool isEmpty()
        {
            return players.Count == 0;
        }

        private void Update()
        {
            players.Clear();
            string[] lines = Array.Empty<string>();
            try
            {
                lines = File.ReadAllText(file).Split('\n');
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
                players.Add(name);
            }

            OnUpdate?.Invoke(this, new EventArgs());
        }

        private void Watcher_Changed(object sender, FileSystemEventArgs e)
        {
            Task.Run(() =>
            {
                Task.Delay(1000).Wait();
                Update();
            });
        }

        public void Dispose()
        {
            watcher?.Dispose();
        }
    }
}