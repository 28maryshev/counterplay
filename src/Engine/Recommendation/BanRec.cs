using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record BanRec(int ChampionId, double Score, string[] Reasons);
