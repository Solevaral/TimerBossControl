using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using TerrariaApi.Server;
using TShockAPI;

namespace BossGate
{
    /// <summary>
    /// BossGate — последовательная разблокировка боссов по реальному времени.
    /// Вся логика серверная: клиенту не доверяем ни в одной точке.
    /// </summary>
    [ApiVersion(2, 1)]
    public class BossGatePlugin : TerrariaPlugin
    {
        public override string Name => "BossGate";
        public override string Author => "Solevara";
        public override string Description => "Открывает боссов по одному раз в N реальных часов";
        public override Version Version => new Version(1, 0, 0);

        /// <summary>Права: обычные игроки.</summary>
        public const string PermUse = "bossgate.use";
        /// <summary>Права: администрирование (unlock/lock/reload).</summary>
        public const string PermAdmin = "bossgate.admin";

        internal static BossGatePlugin Instance;

        internal BossGateConfig Config = BossGateConfig.CreateDefault();
        internal BossGateState State;

        private BossGateDb _db;
        private int _worldId;
        private bool _ready;                 // база подгружена, можно работать
        private bool _reminderSent;          // напоминание за час по текущему боссу уже отправлено
        private DateTime _nextTick = DateTime.MinValue;

        // Быстрые индексы, перестраиваются при загрузке конфига.
        private readonly Dictionary<int, int> _npcToBoss = new Dictionary<int, int>();   // npc id -> индекс босса
        private readonly Dictionary<int, int> _npcToItem = new Dictionary<int, int>();   // npc id -> предмет призыва

        private string ConfigPath => Path.Combine(TShock.SavePath, "BossGate.json");

        public BossGatePlugin(Main game) : base(game)
        {
            // Хотим отработать раньше большинства плагинов, чтобы гасить спавн первыми.
            Order = -10;
        }

        public override void Initialize()
        {
            Instance = this;

            LoadConfig(out _);

            ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
            ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
            ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
            ServerApi.Hooks.NpcSetDefaults.Register(this, OnNpcSetDefaults);
            ServerApi.Hooks.NetGetData.Register(this, OnGetData);

            BossGateCommands.Register();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
                ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
                ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
                ServerApi.Hooks.NpcSetDefaults.Deregister(this, OnNpcSetDefaults);
                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);

                BossGateCommands.Deregister();
                Instance = null;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // Конфиг
        // ------------------------------------------------------------------

        /// <summary>Загружает конфиг. Никогда не бросает исключений наружу.</summary>
        internal bool LoadConfig(out string error)
        {
            error = null;
            try
            {
                Config = BossGateConfig.Read(ConfigPath, out error);
            }
            catch (Exception ex)
            {
                // Совсем крайний случай — работаем на дефолте, сервер не роняем.
                Config = BossGateConfig.CreateDefault();
                error = ex.Message;
            }

            RebuildIndex();

            if (!string.IsNullOrEmpty(error))
            {
                TShock.Log.ConsoleError("[BossGate] Проблемы с конфигом: " + error +
                                        ". Используются значения по умолчанию для проблемных полей.");
                return false;
            }
            return true;
        }

        private void RebuildIndex()
        {
            _npcToBoss.Clear();
            _npcToItem.Clear();

            for (int i = 0; i < Config.Bosses.Count; i++)
            {
                var boss = Config.Bosses[i];
                foreach (var npcId in boss.NpcIds)
                    _npcToBoss[npcId] = i;                 // при дублях побеждает более поздняя запись

                foreach (var pair in boss.SummonItems)
                    _npcToItem[pair.Key] = pair.Value;
            }

            // Состояние могло указывать на больший список боссов, чем есть сейчас.
            if (State != null && State.UnlockedCount > Config.Bosses.Count)
            {
                State.UnlockedCount = Config.Bosses.Count;
                SaveState();
            }
        }

        // ------------------------------------------------------------------
        // Состояние и таймер
        // ------------------------------------------------------------------

        private void OnPostInitialize(EventArgs args)
        {
            try
            {
                _worldId = Main.worldID;
                _db = new BossGateDb(TShock.DB);
                State = _db.LoadOrCreate(_worldId);
                _ready = true;

                // Догоняем время, которое прошло, пока сервер был выключен (без спама в чат).
                ProcessUnlocks(false);

                TShock.Log.ConsoleInfo(string.Format(
                    "[BossGate] Мир {0}: открыто боссов {1}/{2}, интервал {3} ч.",
                    _worldId, State.UnlockedCount, Config.Bosses.Count, Config.UnlockIntervalHours));
            }
            catch (Exception ex)
            {
                _ready = false;
                TShock.Log.ConsoleError("[BossGate] Не удалось инициализировать БД: " + ex.Message +
                                        ". Ограничения выключены, чтобы не ломать сервер.");
            }
        }

        internal void SaveState()
        {
            if (!_ready || State == null) return;
            try
            {
                _db.Save(_worldId, State);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[BossGate] Ошибка сохранения состояния: " + ex.Message);
            }
        }

        internal TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, Config.UnlockIntervalHours));

        /// <summary>Момент следующей разблокировки (UTC). Актуален, только если открыты не все боссы.</summary>
        internal DateTime NextUnlockUtc => State == null ? DateTime.MaxValue : State.LastUnlockUtc + Interval;

        internal bool AllUnlocked => State == null || State.UnlockedCount >= Config.Bosses.Count;

        /// <summary>Открывает всех боссов, срок которых уже наступил.</summary>
        private void ProcessUnlocks(bool announce)
        {
            if (!_ready || State == null || Config.Bosses.Count == 0) return;

            var now = DateTime.UtcNow;
            var interval = Interval;

            while (State.UnlockedCount < Config.Bosses.Count && now - State.LastUnlockUtc >= interval)
            {
                // Прибавляем ровно интервал, а не "сейчас" — так расписание не дрейфует.
                State.LastUnlockUtc += interval;
                State.UnlockedCount++;
                _reminderSent = false;

                var boss = Config.Bosses[State.UnlockedCount - 1];
                if (announce)
                    Broadcast(Format(Config.Messages.UnlockBroadcast, boss.DisplayName), 255, 221, 85);
                else
                    TShock.Log.ConsoleInfo("[BossGate] Открыт (оффлайн-догон): " + boss.DisplayName);

                SaveState();
            }
        }

        private void OnUpdate(EventArgs args)
        {
            // Хук зовётся 60 раз в секунду — тяжёлую работу делаем раз в секунду.
            if (DateTime.UtcNow < _nextTick) return;
            _nextTick = DateTime.UtcNow.AddSeconds(1);

            if (!Config.Enabled || !_ready) return;

            try
            {
                ProcessUnlocks(true);
                SendReminderIfNeeded();
                SweepBlockedNpcs();
                GuardHardmode();
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[BossGate] Ошибка в цикле обновления: " + ex.Message);
            }
        }

        /// <summary>Напоминание за час до открытия следующего босса.</summary>
        private void SendReminderIfNeeded()
        {
            if (!Config.AnnounceHourBefore || _reminderSent || AllUnlocked) return;

            var left = NextUnlockUtc - DateTime.UtcNow;
            if (left <= TimeSpan.FromHours(1) && left > TimeSpan.Zero)
            {
                var boss = Config.Bosses[State.UnlockedCount];
                Broadcast(Format(Config.Messages.UnlockReminder, boss.DisplayName, FormatSpan(left)), 255, 221, 85);
                _reminderSent = true;
            }
        }

        /// <summary>
        /// Страховка: если закрытый босс всё же оказался на карте (сторонний плагин,
        /// нестандартный путь спавна) — гасим его до синхронизации следующим тиком.
        /// </summary>
        private void SweepBlockedNpcs()
        {
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                var npc = Main.npc[i];
                if (npc == null || !npc.active) continue;

                int bossIndex;
                if (!TryGetBossIndex(npc.netID, out bossIndex)) continue;
                if (IsUnlocked(bossIndex)) continue;

                DespawnNpc(i);
                if (Config.LogBlockedAttempts)
                    TShock.Log.ConsoleInfo("[BossGate] Погашен закрытый босс на карте: " +
                                           Config.Bosses[bossIndex].DisplayName + " (netID " + npc.netID + ")");
            }
        }

        /// <summary>Пока Стена Плоти закрыта — хардмоду не бывать.</summary>
        private void GuardHardmode()
        {
            if (!Config.BlockWallOfFleshHardmode || !Main.hardMode) return;

            int wofIndex;
            if (!TryGetBossIndex(NPCID.WallofFlesh, out wofIndex)) return;
            if (IsUnlocked(wofIndex)) return;

            Main.hardMode = false;
            TShock.Log.ConsoleError("[BossGate] Хардмод был включён при закрытой Стене Плоти — откатываю Main.hardMode.");
        }

        // ------------------------------------------------------------------
        // Проверки доступности
        // ------------------------------------------------------------------

        internal bool TryGetBossIndex(int npcId, out int bossIndex)
        {
            return _npcToBoss.TryGetValue(npcId, out bossIndex);
        }

        /// <summary>Открыт ли босс с таким индексом в списке конфига.</summary>
        internal bool IsUnlocked(int bossIndex)
        {
            if (!Config.Enabled) return true;
            if (!_ready || State == null) return true;   // без БД ничего не режем
            return bossIndex < State.UnlockedCount;
        }

        /// <summary>Заблокирован ли NPC с таким netID; заодно отдаёт запись босса.</summary>
        internal bool IsNpcBlocked(int npcId, out BossEntry entry)
        {
            entry = null;
            if (!Config.Enabled || !_ready) return false;

            int index;
            if (!TryGetBossIndex(npcId, out index)) return false;
            if (IsUnlocked(index)) return false;

            entry = Config.Bosses[index];
            return true;
        }

        // ------------------------------------------------------------------
        // Хуки спавна NPC
        // ------------------------------------------------------------------

        /// <summary>
        /// Самая ранняя точка: NPC ещё только получает свои дефолты.
        /// Гасим здесь — тогда босс не появится вообще и клиенты о нём не узнают.
        /// Ловит и естественный ночной спавн (Глаз Ктулху, механические),
        /// и спавн из ивентов, и Стену Плоти от вуду-куклы.
        /// </summary>
        private void OnNpcSetDefaults(SetDefaultsEventArgs<NPC, int> args)
        {
            if (args.Handled || args.Object == null) return;

            try
            {
                BossEntry entry;
                if (!IsNpcBlocked(args.Info, out entry)) return;

                args.Object.SetDefaults(0);   // превращаем в "пустой" NPC
                args.Handled = true;

                if (Config.LogBlockedAttempts)
                    TShock.Log.ConsoleInfo("[BossGate] Заблокирован спавн: " + entry.DisplayName +
                                           " (netID " + args.Info + ")");
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[BossGate] Ошибка в NpcSetDefaults: " + ex.Message);
            }
        }

        /// <summary>Второй рубеж: если NPC всё-таки дошёл до NewNPC — отменяем спавн.</summary>
        private void OnNpcSpawn(NpcSpawnEventArgs args)
        {
            if (args.Handled) return;

            try
            {
                if (args.Npc < 0 || args.Npc >= Main.maxNPCs) return;
                var npc = Main.npc[args.Npc];
                if (npc == null) return;

                BossEntry entry;
                if (!IsNpcBlocked(npc.netID, out entry)) return;

                DespawnNpc(args.Npc);
                args.Handled = true;

                if (Config.LogBlockedAttempts)
                    TShock.Log.ConsoleInfo("[BossGate] Отменён NewNPC: " + entry.DisplayName);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[BossGate] Ошибка в NpcSpawn: " + ex.Message);
            }
        }

        /// <summary>Полностью убирает NPC со слота и сообщает об этом клиентам.</summary>
        private static void DespawnNpc(int index)
        {
            var npc = Main.npc[index];
            npc.active = false;
            npc.life = 0;
            npc.netSkip = -1;
            npc.type = 0;
            NetMessage.SendData((int)PacketTypes.NpcUpdate, -1, -1, NetworkText.Empty, index);
        }

        // ------------------------------------------------------------------
        // Хук пакетов: предметы призыва
        // ------------------------------------------------------------------

        private void OnGetData(GetDataEventArgs args)
        {
            if (args.Handled || !Config.Enabled || !_ready) return;

            try
            {
                switch (args.MsgID)
                {
                    case PacketTypes.SpawnBossorInvasion:
                        HandleSpawnBoss(args);
                        break;

                    case PacketTypes.ItemDrop:
                        HandleItemDrop(args);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Битый пакет — не наша проблема, но и падать из-за него нельзя.
                TShock.Log.ConsoleError("[BossGate] Ошибка разбора пакета " + args.MsgID + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Пакет 61: клиент использовал предмет призыва (или начал ивент).
        /// Формат: short playerId, short type (тип NPC для боссов, отрицательное — ивент).
        /// </summary>
        private void HandleSpawnBoss(GetDataEventArgs args)
        {
            if (args.Length < 4) return;

            short packetPlayer;
            short bossType;
            using (var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
            {
                packetPlayer = reader.ReadInt16();
                bossType = reader.ReadInt16();
            }

            if (bossType <= 0) return;   // ивенты (вторжения) плагин не трогает

            BossEntry entry;
            if (!IsNpcBlocked(bossType, out entry)) return;

            args.Handled = true;         // предмет не будет израсходован сервером

            var player = TShock.Players[args.Msg.whoAmI];
            if (player == null) return;

            // Подстраховка от подмены индекса игрока в пакете.
            if (packetPlayer != args.Msg.whoAmI && Config.LogBlockedAttempts)
                TShock.Log.ConsoleInfo("[BossGate] " + player.Name + " прислал чужой playerId в пакете 61.");

            RefundSummonItem(player, bossType);
            player.SendMessage(Format(Config.Messages.Blocked, entry.DisplayName), 255, 85, 85);

            if (Config.LogBlockedAttempts)
                TShock.Log.ConsoleInfo("[BossGate] " + player.Name + " пытался призвать закрытого босса: " +
                                       entry.DisplayName);
        }

        /// <summary>
        /// Пакеты 21/90: выброс предмета. Нужен только для Вуду-куклы проводника —
        /// иначе игрок сможет запустить хардмод, кинув её в лаву.
        /// Формат: short id, float x, float y, float vx, float vy, short stack, byte prefix, byte noDelay, short type.
        /// </summary>
        private void HandleItemDrop(GetDataEventArgs args)
        {
            if (!Config.BlockWallOfFleshHardmode) return;
            if (args.Length < 24) return;

            short stack;
            short itemType;
            using (var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
            {
                reader.ReadInt16();      // индекс предмета в мире
                reader.ReadSingle();     // x
                reader.ReadSingle();     // y
                reader.ReadSingle();     // vx
                reader.ReadSingle();     // vy
                stack = reader.ReadInt16();
                reader.ReadByte();       // prefix
                reader.ReadByte();       // noDelay
                itemType = reader.ReadInt16();
            }

            if (itemType != ItemID.GuideVoodooDoll || stack <= 0) return;

            BossEntry entry;
            if (!IsNpcBlocked(NPCID.WallofFlesh, out entry)) return;

            args.Handled = true;

            var player = TShock.Players[args.Msg.whoAmI];
            if (player == null) return;

            player.GiveItem(ItemID.GuideVoodooDoll, stack);
            player.SendMessage(Format(Config.Messages.Blocked, entry.DisplayName), 255, 85, 85);
            player.SendMessage(Config.Messages.ItemRefunded, 255, 255, 255);

            if (Config.LogBlockedAttempts)
                TShock.Log.ConsoleInfo("[BossGate] " + player.Name + " пытался выбросить вуду-куклу проводника.");
        }

        /// <summary>Возвращает игроку предмет призыва, если он известен для этого босса.</summary>
        private void RefundSummonItem(TSPlayer player, int bossType)
        {
            int itemId;
            if (!_npcToItem.TryGetValue(bossType, out itemId) || itemId <= 0) return;

            try
            {
                player.GiveItem(itemId, 1);
                player.SendMessage(Config.Messages.ItemRefunded, 255, 255, 255);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError("[BossGate] Не удалось вернуть предмет " + itemId + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Утилиты
        // ------------------------------------------------------------------

        internal static void Broadcast(string message, byte r, byte g, byte b)
        {
            if (string.IsNullOrEmpty(message)) return;
            TShock.Utils.Broadcast(message, r, g, b);
        }

        /// <summary>string.Format, который не падает на кривом шаблоне из конфига.</summary>
        internal static string Format(string template, params object[] args)
        {
            if (string.IsNullOrEmpty(template)) return "";
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        /// <summary>Человекочитаемый остаток времени на русском.</summary>
        internal static string FormatSpan(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;

            var parts = new List<string>();
            if (span.Days > 0) parts.Add(span.Days + " д");
            if (span.Hours > 0) parts.Add(span.Hours + " ч");
            if (span.Minutes > 0) parts.Add(span.Minutes + " мин");
            if (parts.Count == 0) parts.Add(span.Seconds + " с");
            return string.Join(" ", parts);
        }
    }
}
