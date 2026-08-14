using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace BossGate
{
    /// <summary>
    /// Один "шаг" прогрессии. Может объединять несколько боссов (например, механических),
    /// тогда они открываются одновременно.
    /// </summary>
    public class BossEntry
    {
        /// <summary>Технический ключ (латиницей), используется в логах и командах.</summary>
        public string Key = "";

        /// <summary>Отображаемое имя для чата и команды /bosses.</summary>
        public string DisplayName = "";

        /// <summary>
        /// Все NPC id, относящиеся к этому боссу, включая сегменты и части тела
        /// (Пожиратель миров: голова/тело/хвост, Скелетрон Прайм: руки, и т.д.).
        /// Любой из них будет погашен, пока босс закрыт.
        /// </summary>
        public List<int> NpcIds = new List<int>();

        /// <summary>
        /// Карта "NPC id босса -> id предмета призыва". Нужна, чтобы вернуть игроку
        /// потраченный предмет. Боссы без предмета призыва (Плантера, Императрица,
        /// Культист) здесь просто пустые.
        /// </summary>
        public Dictionary<int, int> SummonItems = new Dictionary<int, int>();
    }

    /// <summary>Тексты сообщений. Все поддерживают подстановки, см. комментарии.</summary>
    public class BossGateMessages
    {
        /// <summary>{0} — название босса.</summary>
        public string Blocked = "[c/ff5555:Босс ещё закрыт:] {0}. Он откроется позже — смотри /bosstime.";

        /// <summary>Сообщение при возврате предмета.</summary>
        public string ItemRefunded = "Предмет призыва возвращён в инвентарь.";

        /// <summary>{0} — название босса.</summary>
        public string UnlockBroadcast = "[c/ffdd55:Открыт новый босс:] {0}! Удачной охоты.";

        /// <summary>{0} — название босса, {1} — сколько осталось (уже отформатировано).</summary>
        public string UnlockReminder = "[c/ffdd55:Через {1}] откроется босс: {0}";

        /// <summary>Заголовок списка боссов.</summary>
        public string ListHeader = "Прогрессия боссов ({0} из {1} открыто):";

        /// <summary>{0} — номер, {1} — имя.</summary>
        public string ListUnlocked = "  [c/55ff55:{0}. {1} — открыт]";

        /// <summary>{0} — номер, {1} — имя.</summary>
        public string ListLocked = "  [c/ff5555:{0}. {1} — закрыт]";

        /// <summary>{0} — имя следующего босса, {1} — отформатированное время.</summary>
        public string TimeLeft = "Следующий босс ({0}) откроется через {1}.";

        public string AllUnlocked = "Все боссы уже открыты.";

        public string Disabled = "Ограничение боссов сейчас выключено — доступны все.";

        /// <summary>{0} — имя босса.</summary>
        public string AdminUnlocked = "Босс принудительно открыт: {0}";

        /// <summary>{0} — количество, {1} — новое число открытых.</summary>
        public string AdminLocked = "Откат на {0}. Теперь открыто боссов: {1}";

        public string ConfigReloaded = "Конфиг BossGate перезагружен.";
    }

    public class BossGateConfig
    {
        /// <summary>Полностью включает/выключает работу плагина.</summary>
        public bool Enabled = true;

        /// <summary>Через сколько часов реального времени открывается следующий босс.</summary>
        public int UnlockIntervalHours = 72;

        /// <summary>
        /// Дополнительная защита от преждевременного хардмода: пока Стена Плоти закрыта,
        /// плагин блокирует выброс Вуду-куклы проводника и откатывает Main.hardMode,
        /// если тот всё-таки включился.
        /// </summary>
        public bool BlockWallOfFleshHardmode = true;

        /// <summary>Напоминать в чат за час до открытия следующего босса.</summary>
        public bool AnnounceHourBefore = true;

        /// <summary>Писать в консоль/лог каждую заблокированную попытку призыва.</summary>
        public bool LogBlockedAttempts = true;

        /// <summary>
        /// Требовать право bossgate.use для /bosses и /bosstime.
        /// false (по умолчанию) — команды доступны всем, включая гостей.
        /// </summary>
        public bool RequireUsePermission = false;

        /// <summary>Порядок боссов. Первый в списке открывается сразу при старте отсчёта.</summary>
        public List<BossEntry> Bosses = new List<BossEntry>();

        public BossGateMessages Messages = new BossGateMessages();

        /// <summary>Ванильная прогрессия по умолчанию.</summary>
        public static BossGateConfig CreateDefault()
        {
            var c = new BossGateConfig();
            c.Bosses = new List<BossEntry>
            {
                Boss("king_slime", "Королевский слизень", new[] { 50 },
                    Map(50, 560)),                                   // Корона слизней
                Boss("eye_of_cthulhu", "Глаз Ктулху", new[] { 4 },
                    Map(4, 43)),                                     // Подозрительный глаз
                Boss("eow_boc", "Пожиратель миров / Мозг Ктулху",
                    new[] { 13, 14, 15, 266, 267 },                  // сегменты червя + мозг с криперами
                    Map(13, 70, 266, 1331)),                         // Червивая еда / Кровавый позвоночник
                Boss("queen_bee", "Пчелиная матка", new[] { 222 },
                    Map(222, 1133)),                                 // Мерзкий улей
                Boss("skeletron", "Скелетрон", new[] { 35, 36 },
                    Map(35, 1307)),                                  // Вуду-кукла портного
                Boss("deerclops", "Лютый олень", new[] { 668 },
                    Map(668, 4986)),                                 // Оленья штучка
                Boss("wall_of_flesh", "Стена Плоти", new[] { 113, 114, 115, 116 },
                    Map(113, 267)),                                  // Вуду-кукла проводника
                Boss("queen_slime", "Королева слизней", new[] { 657 },
                    Map(657, 4988)),                                 // Желатиновый кристалл
                Boss("mech_bosses", "Механические боссы (Близнецы / Уничтожитель / Скелетрон Прайм)",
                    new[] { 125, 126, 134, 135, 136, 127, 128, 129, 130, 131 },
                    Map(125, 544, 134, 556, 127, 557)),              // Механический глаз / червь / череп
                Boss("plantera", "Плантера", new[] { 262, 263, 264 }, Map()),
                Boss("golem", "Голем", new[] { 245, 246, 247, 248, 249 },
                    Map(245, 1293)),                                 // Лихзардский энергоэлемент
                Boss("duke_fishron", "Герцог Рыброн", new[] { 370 },
                    Map(370, 2673)),                                 // Трюфельный червь
                Boss("empress_of_light", "Императрица Света", new[] { 636 }, Map()),
                Boss("lunatic_cultist", "Лунатик-культист", new[] { 439, 440 }, Map()),
                Boss("moon_lord", "Лунный Лорд", new[] { 396, 397, 398, 400, 401 },
                    Map(398, 3601))                                  // Небесная печать
            };
            return c;
        }

        private static BossEntry Boss(string key, string name, int[] npcIds, Dictionary<int, int> items)
        {
            return new BossEntry
            {
                Key = key,
                DisplayName = name,
                NpcIds = new List<int>(npcIds),
                SummonItems = items
            };
        }

        /// <summary>Хелпер: пары "npc id, item id".</summary>
        private static Dictionary<int, int> Map(params int[] pairs)
        {
            var d = new Dictionary<int, int>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                d[pairs[i]] = pairs[i + 1];
            return d;
        }

        /// <summary>
        /// Читает конфиг. Любая ошибка (нет файла, битый JSON, мусор в полях) не должна
        /// ронять сервер: возвращаем дефолт и пишем причину в out-параметр.
        /// </summary>
        public static BossGateConfig Read(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    var def = CreateDefault();
                    def.Write(path);
                    return def;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                var cfg = JsonConvert.DeserializeObject<BossGateConfig>(json);
                if (cfg == null)
                {
                    error = "конфиг пуст или не разобран";
                    return CreateDefault();
                }

                cfg.Validate(ref error);
                return cfg;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return CreateDefault();
            }
        }

        /// <summary>Чинит заведомо неверные значения, чтобы плагин не падал на них потом.</summary>
        public void Validate(ref string error)
        {
            var problems = new List<string>();

            if (UnlockIntervalHours <= 0)
            {
                problems.Add("UnlockIntervalHours <= 0, взято 72");
                UnlockIntervalHours = 72;
            }

            if (Messages == null)
            {
                problems.Add("отсутствует секция Messages, взяты тексты по умолчанию");
                Messages = new BossGateMessages();
            }

            if (Bosses == null || Bosses.Count == 0)
            {
                problems.Add("пустой список боссов, взята ванильная прогрессия");
                Bosses = CreateDefault().Bosses;
            }
            else
            {
                // Выкидываем записи без NPC id — они всё равно ничего не блокируют.
                for (int i = Bosses.Count - 1; i >= 0; i--)
                {
                    var b = Bosses[i];
                    if (b == null || b.NpcIds == null || b.NpcIds.Count == 0)
                    {
                        problems.Add("запись боссов #" + (i + 1) + " без NpcIds — пропущена");
                        Bosses.RemoveAt(i);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(b.DisplayName))
                        b.DisplayName = string.IsNullOrWhiteSpace(b.Key) ? "Босс #" + (i + 1) : b.Key;
                    if (b.SummonItems == null)
                        b.SummonItems = new Dictionary<int, int>();
                }

                if (Bosses.Count == 0)
                {
                    problems.Add("не осталось валидных боссов, взята ванильная прогрессия");
                    Bosses = CreateDefault().Bosses;
                }
            }

            if (problems.Count > 0)
                error = string.Join("; ", problems);
        }

        public void Write(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
        }
    }
}
