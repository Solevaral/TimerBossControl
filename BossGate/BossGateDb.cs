using System;
using System.Data;
using MySql.Data.MySqlClient;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.DB.Queries;

namespace BossGate
{
    /// <summary>Состояние прогрессии для одного мира.</summary>
    public class BossGateState
    {
        /// <summary>Сколько боссов уже открыто (индексы 0..UnlockedCount-1 из списка конфига).</summary>
        public int UnlockedCount;

        /// <summary>Момент старта отсчёта (первый запуск на этом мире), UTC.</summary>
        public DateTime StartUtc;

        /// <summary>
        /// Момент последней разблокировки, UTC. От него отсчитывается следующая.
        /// Хранится абсолютным временем, поэтому таймер "тикает" и пока сервер выключен.
        /// </summary>
        public DateTime LastUnlockUtc;

        /// <summary>Таймер остановлен админом (/boss timestop).</summary>
        public bool Paused;

        /// <summary>
        /// Сколько оставалось до открытия в момент паузы. Пока таймер стоит, показываем
        /// именно это значение, и именно его меняют addtime/removetime.
        /// </summary>
        public TimeSpan PausedRemaining;
    }

    /// <summary>Хранилище состояния в базе TShock (по умолчанию SQLite: tshock/tshock.sqlite).</summary>
    public class BossGateDb
    {
        private const string TableName = "BossGateState";
        private readonly IDbConnection _db;

        public BossGateDb(IDbConnection db)
        {
            _db = db;

            // Билдер запросов подбираем под фактическую БД сервера.
            IQueryBuilder builder;
            switch (_db.GetSqlType())
            {
                case SqlType.Sqlite:
                    builder = new SqliteQueryBuilder();
                    break;
                case SqlType.Postgres:
                    builder = new PostgresQueryBuilder();
                    break;
                default:
                    builder = new MysqlQueryBuilder();
                    break;
            }

            var creator = new SqlTableCreator(_db, builder);

            // Время храним строкой (тики UTC) — одинаково работает и в SQLite, и в MySQL.
            creator.EnsureTableStructure(new SqlTable(TableName,
                new SqlColumn("WorldId", MySqlDbType.Int32) { Primary = true, Unique = true },
                new SqlColumn("UnlockedCount", MySqlDbType.Int32),
                new SqlColumn("StartUtc", MySqlDbType.Text),
                new SqlColumn("LastUnlockUtc", MySqlDbType.Text),
                new SqlColumn("Paused", MySqlDbType.Int32),
                new SqlColumn("PausedRemaining", MySqlDbType.Text)));
        }

        /// <summary>Читает состояние мира; если записи нет — создаёт новую с отсчётом от текущего момента.</summary>
        public BossGateState LoadOrCreate(int worldId)
        {
            using (var reader = _db.QueryReader(
                "SELECT UnlockedCount, StartUtc, LastUnlockUtc, Paused, PausedRemaining FROM " + TableName +
                " WHERE WorldId = @0", worldId))
            {
                if (reader.Read())
                {
                    var state = new BossGateState
                    {
                        UnlockedCount = reader.Get<int>("UnlockedCount"),
                        StartUtc = ParseUtc(reader.Get<string>("StartUtc")),
                        LastUnlockUtc = ParseUtc(reader.Get<string>("LastUnlockUtc")),
                        Paused = reader.Get<int?>("Paused").GetValueOrDefault() != 0,
                        PausedRemaining = ParseSpan(reader.Get<string>("PausedRemaining"))
                    };
                    if (state.UnlockedCount < 0)
                        state.UnlockedCount = 0;
                    return state;
                }
            }

            var now = DateTime.UtcNow;
            var fresh = new BossGateState
            {
                UnlockedCount = 1,      // первый босс доступен сразу
                StartUtc = now,
                LastUnlockUtc = now
            };
            Save(worldId, fresh);
            return fresh;
        }

        public void Save(int worldId, BossGateState state)
        {
            // Upsert без диалект-специфичного синтаксиса.
            _db.Query("DELETE FROM " + TableName + " WHERE WorldId = @0", worldId);
            _db.Query(
                "INSERT INTO " + TableName +
                " (WorldId, UnlockedCount, StartUtc, LastUnlockUtc, Paused, PausedRemaining)" +
                " VALUES (@0, @1, @2, @3, @4, @5)",
                worldId,
                state.UnlockedCount,
                state.StartUtc.Ticks.ToString(),
                state.LastUnlockUtc.Ticks.ToString(),
                state.Paused ? 1 : 0,
                state.PausedRemaining.Ticks.ToString());
        }

        private static TimeSpan ParseSpan(string raw)
        {
            long ticks;
            if (!long.TryParse(raw, out ticks) || ticks < 0)
                return TimeSpan.Zero;
            return TimeSpan.FromTicks(ticks);
        }

        private static DateTime ParseUtc(string raw)
        {
            long ticks;
            if (!long.TryParse(raw, out ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
                return DateTime.UtcNow;
            return new DateTime(ticks, DateTimeKind.Utc);
        }
    }
}
