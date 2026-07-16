using MatchLogic;

namespace Omukade.Cheyenne.Matchmaking
{
    public interface IMatchmakingSwimlane
    {
        GameplayType gameplayType { get; init; }
        GameMode format { get; init; }

        /// <summary>
        /// The callback that will be fired when two players are matched together and a game should be started between them.
        /// </summary>
        MatchmakingCompleteCallback MatchMakingCompleteCallback { get; init; }

        /// <summary>
        /// Adds a new player to the matchmaking queue.
        /// </summary>
        /// <param name="playerMetadata">The player to enter into matchmaking.</param>
        void EnqueuePlayer(PlayerMetadata playerMetadata);

        /// <summary>
        /// Removes a player from matchmaking (eg, disconnected, cancelled, entered into another game). If the player is not in the matchmaking queue, this method will return immediately.
        /// </summary>
        /// <param name="playerMetadata">The player to attempt to remove from matchmaking.</param>
        void RemovePlayerFromMatchmaking(PlayerMetadata playerMetadata);

        /// <summary>
        /// Performs a periodic tick to process queued players (eg, perform ELO-based matchmaking once enough players are available).
        /// </summary>
        void Tick() { }
    }

    public delegate void MatchmakingCompleteCallback(IMatchmakingSwimlane swimlane, PlayerMetadata player1, PlayerMetadata player2);
}
