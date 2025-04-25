using System.Runtime.CompilerServices;

namespace Omukade.Cheyenne
{
    internal static class RecordPlayerData
    {
        static RecordPlayerData()
        {
            if (!Directory.Exists(DataDirectory))
            {
                Directory.CreateDirectory(DataDirectory);
            }
        }

        // Token: 0x06000080 RID: 128 RVA: 0x00005B44 File Offset: 0x00003D44
        public static void Connected(string player, string remoteIP)
        {
            WriteData(player, $"Connected: {remoteIP} (port: {Program.config.HttpPort})");
        }

        // Token: 0x06000081 RID: 129 RVA: 0x00005B99 File Offset: 0x00003D99
        public static void Disconnected(string player)
        {
            WriteData(player, $"Disconnected: (port: {Program.config.HttpPort})");
        }

        // Token: 0x06000082 RID: 130 RVA: 0x00005BA6 File Offset: 0x00003DA6
        public static void StartMatch(string player, string opponent)
        {
            WriteData(player, "StartMatch: " + opponent);
        }

        // Token: 0x06000083 RID: 131 RVA: 0x00005BBC File Offset: 0x00003DBC
        public static void EndMatch(string player, string opponent, bool isWin)
        {
            WriteData(player, $"EndMatch: {opponent} (isWin: ${isWin})");
        }

        private static void WriteData(string player, string data)
        {
            if (string.IsNullOrEmpty(player))
            {
                return;
            }
            string text = Path.Combine(DataDirectory, player + ".txt");
            File.AppendAllText(text, $"[{DateTime.Now:R}] - {data}\n");
        }

        private static string DataDirectory = Program.config.RecordPlayerDataDirectory;
    }
}