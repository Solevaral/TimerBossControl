using System;
using System.Collections.Generic;
using TShockAPI;

namespace BossGate
{
    /// <summary>Все чат-команды плагина.</summary>
    internal static class BossGateCommands
    {
        private static readonly List<Command> Registered = new List<Command>();

        public static void Register()
        {
            Deregister();

            var cfg = BossGatePlugin.Instance != null ? BossGatePlugin.Instance.Config : null;
            var requireUse = cfg != null && cfg.RequireUsePermission;

            // Информационные команды: по умолчанию без права, доступны всем.
            Add(Make(requireUse ? BossGatePlugin.PermUse : null, CmdBosses,
                "Показывает список боссов: какие открыты, какие закрыты.", "bosses"));

            Add(Make(requireUse ? BossGatePlugin.PermUse : null, CmdBossTime,
                "Показывает, сколько осталось до открытия следующего босса.", "bosstime"));

            // Административные.
            Add(Make(BossGatePlugin.PermAdmin, CmdBossUnlock,
                "Открывает следующего босса вручную.", "bossunlock"));

            Add(Make(BossGatePlugin.PermAdmin, CmdBossLock,
                "/bosslock <n> — откатывает счётчик открытых боссов на n.", "bosslock"));

            Add(Make(BossGatePlugin.PermAdmin, CmdBossReload,
                "Перезагружает конфиг BossGate.", "bossreload"));

            // Единая команда с подкомандами. Права проверяются внутри, чтобы информационные
            // подкоманды остались доступны всем.
            Add(Make(null, CmdBoss,
                "/boss <list|time|addtime|removetime|timestop|timestart|unlock|lock|reload>", "boss"));
        }

        // ------------------------------------------------------------------
        // /boss <подкоманда>
        // ------------------------------------------------------------------

        private static void CmdBoss(CommandArgs args)
        {
            var sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";
            // Остальные параметры — аргументы подкоманды.
            var rest = args.Parameters.Count > 1
                ? args.Parameters.GetRange(1, args.Parameters.Count - 1)
                : new List<string>();

            switch (sub)
            {
                case "list":
                case "bosses":
                case "список":
                    CmdBosses(args);
                    return;

                case "time":
                case "время":
                    CmdBossTime(args);
                    return;

                case "addtime":
                case "removetime":
                    if (!RequireAdmin(args)) return;
                    ShiftTime(args, rest, sub == "addtime");
                    return;

                case "timestop":
                case "stop":
                case "pause":
                    if (!RequireAdmin(args)) return;
                    StopTimer(args);
                    return;

                case "timestart":
                case "start":
                case "resume":
                    if (!RequireAdmin(args)) return;
                    StartTimer(args);
                    return;

                case "unlock":
                    if (!RequireAdmin(args)) return;
                    CmdBossUnlock(args);
                    return;

                case "lock":
                    if (!RequireAdmin(args)) return;
                    args.Parameters.RemoveAt(0);      // чтобы /bosslock увидел количество первым
                    CmdBossLock(args);
                    return;

                case "reload":
                    if (!RequireAdmin(args)) return;
                    CmdBossReload(args);
                    return;

                default:
                    SendUsage(args);
                    return;
            }
        }

        private static void SendUsage(CommandArgs args)
        {
            args.Player.SendInfoMessage("Команды BossGate:");
            args.Player.SendInfoMessage("  /boss list — список боссов");
            args.Player.SendInfoMessage("  /boss time — сколько осталось до следующего");

            if (!args.Player.HasPermission(BossGatePlugin.PermAdmin)) return;

            args.Player.SendInfoMessage("  /boss addtime 1h 30m — отложить открытие");
            args.Player.SendInfoMessage("  /boss removetime 1h — приблизить открытие");
            args.Player.SendInfoMessage("  /boss timestop — остановить таймер");
            args.Player.SendInfoMessage("  /boss timestart — продолжить таймер");
            args.Player.SendInfoMessage("  /boss unlock | /boss lock <n> | /boss reload");
        }

        private static bool RequireAdmin(CommandArgs args)
        {
            if (args.Player.HasPermission(BossGatePlugin.PermAdmin)) return true;
            args.Player.SendErrorMessage("Нужно право " + BossGatePlugin.PermAdmin + ".");
            return false;
        }

        /// <summary>Общая часть addtime/removetime.</summary>
        private static void ShiftTime(CommandArgs args, List<string> duration, bool add)
        {
            var p = BossGatePlugin.Instance;
            if (p == null || p.State == null)
            {
                args.Player.SendErrorMessage("BossGate ещё не инициализирован.");
                return;
            }

            if (p.AllUnlocked)
            {
                args.Player.SendInfoMessage(p.Config.Messages.AllUnlocked);
                return;
            }

            TimeSpan span;
            if (!BossGatePlugin.TryParseDuration(duration, out span))
            {
                args.Player.SendErrorMessage(p.Config.Messages.BadDuration);
                return;
            }

            p.ShiftTimer(add ? span : -span);

            var boss = p.NextBoss;
            var template = add ? p.Config.Messages.TimeAdded : p.Config.Messages.TimeRemoved;
            BossGatePlugin.Broadcast(BossGatePlugin.Format(template,
                BossGatePlugin.FormatSpan(span), boss.DisplayName, BossGatePlugin.FormatSpan(p.TimeLeft)),
                255, 221, 85);

            TShock.Log.ConsoleInfo("[BossGate] " + args.Player.Name + (add ? " добавил " : " убрал ") +
                                   BossGatePlugin.FormatSpan(span) + ", осталось " +
                                   BossGatePlugin.FormatSpan(p.TimeLeft));
        }

        private static void StopTimer(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null || p.State == null)
            {
                args.Player.SendErrorMessage("BossGate ещё не инициализирован.");
                return;
            }

            if (p.AllUnlocked)
            {
                args.Player.SendInfoMessage(p.Config.Messages.AllUnlocked);
                return;
            }

            if (!p.PauseTimer())
            {
                args.Player.SendInfoMessage(p.Config.Messages.TimerAlreadyStopped);
                return;
            }

            BossGatePlugin.Broadcast(BossGatePlugin.Format(p.Config.Messages.TimerStopped,
                p.NextBoss.DisplayName, BossGatePlugin.FormatSpan(p.TimeLeft)), 255, 85, 85);

            TShock.Log.ConsoleInfo("[BossGate] " + args.Player.Name + " остановил таймер, осталось " +
                                   BossGatePlugin.FormatSpan(p.TimeLeft));
        }

        private static void StartTimer(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null || p.State == null)
            {
                args.Player.SendErrorMessage("BossGate ещё не инициализирован.");
                return;
            }

            if (!p.ResumeTimer())
            {
                args.Player.SendInfoMessage(p.Config.Messages.TimerAlreadyRunning);
                return;
            }

            if (p.AllUnlocked)
            {
                args.Player.SendSuccessMessage(p.Config.Messages.AllUnlocked);
                return;
            }

            BossGatePlugin.Broadcast(BossGatePlugin.Format(p.Config.Messages.TimerStarted,
                p.NextBoss.DisplayName, BossGatePlugin.FormatSpan(p.TimeLeft)), 85, 255, 85);

            TShock.Log.ConsoleInfo("[BossGate] " + args.Player.Name + " продолжил таймер, осталось " +
                                   BossGatePlugin.FormatSpan(p.TimeLeft));
        }

        /// <summary>permission == null — команда доступна всем без права.</summary>
        private static Command Make(string permission, CommandDelegate callback, string help, params string[] names)
        {
            var cmd = permission == null
                ? new Command(callback, names)
                : new Command(permission, callback, names);
            cmd.HelpText = help;
            return cmd;
        }

        public static void Deregister()
        {
            foreach (var cmd in Registered)
                Commands.ChatCommands.Remove(cmd);
            Registered.Clear();
        }

        private static void Add(Command cmd)
        {
            Commands.ChatCommands.Add(cmd);
            Registered.Add(cmd);
        }

        // ------------------------------------------------------------------

        private static void CmdBosses(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null) return;

            if (!p.Config.Enabled)
            {
                args.Player.SendInfoMessage(p.Config.Messages.Disabled);
                return;
            }

            var unlocked = p.State != null ? p.State.UnlockedCount : p.Config.Bosses.Count;
            args.Player.SendInfoMessage(BossGatePlugin.Format(
                p.Config.Messages.ListHeader, unlocked, p.Config.Bosses.Count));

            for (int i = 0; i < p.Config.Bosses.Count; i++)
            {
                var boss = p.Config.Bosses[i];
                var tmpl = i < unlocked ? p.Config.Messages.ListUnlocked : p.Config.Messages.ListLocked;
                args.Player.SendMessage(BossGatePlugin.Format(tmpl, i + 1, boss.DisplayName), 255, 255, 255);
            }

            if (unlocked < p.Config.Bosses.Count)
                SendTimeLeft(args, p);
        }

        private static void CmdBossTime(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null) return;

            if (!p.Config.Enabled)
            {
                args.Player.SendInfoMessage(p.Config.Messages.Disabled);
                return;
            }

            SendTimeLeft(args, p);
        }

        private static void SendTimeLeft(CommandArgs args, BossGatePlugin p)
        {
            if (p.AllUnlocked)
            {
                args.Player.SendInfoMessage(p.Config.Messages.AllUnlocked);
                return;
            }

            var next = p.NextBoss;
            var left = BossGatePlugin.FormatSpan(p.TimeLeft);
            var template = p.IsPaused ? p.Config.Messages.TimeLeftPaused : p.Config.Messages.TimeLeft;
            args.Player.SendInfoMessage(BossGatePlugin.Format(template, next.DisplayName, left));
        }

        private static void CmdBossUnlock(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null || p.State == null)
            {
                args.Player.SendErrorMessage("BossGate ещё не инициализирован.");
                return;
            }

            if (p.AllUnlocked)
            {
                args.Player.SendInfoMessage(p.Config.Messages.AllUnlocked);
                return;
            }

            var boss = p.NextBoss;
            p.State.UnlockedCount++;
            p.RestartInterval();                      // отсчёт до следующего пойдёт заново

            BossGatePlugin.Broadcast(
                BossGatePlugin.Format(p.Config.Messages.UnlockBroadcast, boss.DisplayName), 255, 221, 85);
            args.Player.SendSuccessMessage(BossGatePlugin.Format(p.Config.Messages.AdminUnlocked, boss.DisplayName));
            TShock.Log.ConsoleInfo("[BossGate] " + args.Player.Name + " открыл босса вручную: " + boss.DisplayName);
        }

        private static void CmdBossLock(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null || p.State == null)
            {
                args.Player.SendErrorMessage("BossGate ещё не инициализирован.");
                return;
            }

            int count = 1;
            if (args.Parameters.Count > 0 && !int.TryParse(args.Parameters[0], out count))
            {
                args.Player.SendErrorMessage("Использование: /bosslock <количество>");
                return;
            }

            if (count <= 0)
            {
                args.Player.SendErrorMessage("Количество должно быть больше нуля.");
                return;
            }

            p.State.UnlockedCount = Math.Max(0, p.State.UnlockedCount - count);
            p.RestartInterval();                      // иначе просроченный таймер сразу вернёт босса

            args.Player.SendSuccessMessage(BossGatePlugin.Format(
                p.Config.Messages.AdminLocked, count, p.State.UnlockedCount));
            TShock.Log.ConsoleInfo("[BossGate] " + args.Player.Name + " откатил прогрессию на " + count +
                                   ", открыто: " + p.State.UnlockedCount);
        }

        private static void CmdBossReload(CommandArgs args)
        {
            var p = BossGatePlugin.Instance;
            if (p == null) return;

            string error;
            var ok = p.LoadConfig(out error);
            Register();   // права на команды могли поменяться

            if (ok)
                args.Player.SendSuccessMessage(p.Config.Messages.ConfigReloaded);
            else
                args.Player.SendErrorMessage("Конфиг перезагружен с ошибками: " + error);
        }
    }
}
