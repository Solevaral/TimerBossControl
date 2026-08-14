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

            var next = p.Config.Bosses[p.State.UnlockedCount];
            var left = p.NextUnlockUtc - DateTime.UtcNow;
            args.Player.SendInfoMessage(BossGatePlugin.Format(
                p.Config.Messages.TimeLeft, next.DisplayName, BossGatePlugin.FormatSpan(left)));
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

            var boss = p.Config.Bosses[p.State.UnlockedCount];
            p.State.UnlockedCount++;
            p.State.LastUnlockUtc = DateTime.UtcNow;   // отсчёт до следующего пойдёт заново
            p.SaveState();

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
            p.State.LastUnlockUtc = DateTime.UtcNow;   // иначе просроченный таймер сразу вернёт босса
            p.SaveState();

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
