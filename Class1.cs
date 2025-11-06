using Mono.Data.Sqlite; // SQLite provider
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using MySql.Data.MySqlClient;   

namespace LevelingPlugin
{
    [ApiVersion(2, 1)]
    public class LevelingPlugin : TerrariaPlugin
    {
        private IDbConnection db;
        private Random rng = new Random();

        public override string Name => "Level System";
        public override string Author => "Jc2";
        public override string Description => "Leveling System with SQLite persistence";
        public override Version Version => new Version(1, 3, 0);

        public LevelingPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.NpcKilled.Register(this, OnNpcKilled);

            Commands.ChatCommands.Add(new Command(MyLevelCommand, "mylevel"));

            Commands.ChatCommands.Add(new Command("level.admin", SetLevelCommand, "setlevel"));
            Commands.ChatCommands.Add(new Command("level.admin", AddLevelCommand, "addlevel"));
            Commands.ChatCommands.Add(new Command("level.admin", MinusLevelCommand, "minuslevel"));
            Commands.ChatCommands.Add(new Command("level.admin", ResetLevelCommand, "resetlevel"));

            ServerApi.Hooks.GamePostInitialize.Register(this, OnGamePostInitialize);
        }

        private void OnGamePostInitialize(EventArgs args)
        {
            InitDB();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NpcKilled.Deregister(this, OnNpcKilled);
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnGamePostInitialize);

                db?.Close();
                db?.Dispose();
                db = null;
            }
            base.Dispose(disposing);
        }

        private void InitDB()
        {
            try
            {
                string dbPath = Path.Combine(TShock.SavePath, "Leveling.sqlite");
                db = new SqliteConnection($"uri=file://{dbPath},Version=3");
                db.Open();

                var table = new SqlTable("PlayerExp",
                    new SqlColumn("UserKey", MySqlDbType.VarChar, 64) { Primary = true, NotNull = true },
                    new SqlColumn("Exp", MySqlDbType.Int32) { NotNull = true, DefaultValue = "0" }
                );

                var creator = new SqlTableCreator(db, new SqliteQueryCreator());
                creator.EnsureTableStructure(table);

                TShock.Log.ConsoleInfo("[LevelSystem] Database initialized successfully.");
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[LevelSystem] Failed to initialize DB: {ex}");
            }
        }

        private string GetPlayerKey(TSPlayer player)
        {
            if (player?.Account != null)
                return $"acc_{player.Account.ID}";
            return $"uuid_{player?.UUID}";
        }

        private int GetExp(string key)
        {
            if (db == null) return 0;

            using (var reader = db.QueryReader("SELECT Exp FROM PlayerExp WHERE UserKey = @0", key))
            {
                if (reader.Read())
                    return reader.Get<int>("Exp");
            }
            return 0;
        }

        private void UpsertExp(string key, int exp)
        {
            if (db == null) return;

            // Try update first
            int rows = db.Query("UPDATE PlayerExp SET Exp = @1 WHERE UserKey = @0", key, exp);

            // If no row was updated, insert a new one
            if (rows == 0)
            {
                db.Query("INSERT INTO PlayerExp (UserKey, Exp) VALUES (@0, @1)", key, exp);
            }
        }

        private void AddExp(string key, int delta)
        {
            int current = GetExp(key);
            int next = current + Math.Max(0, delta);
            UpsertExp(key, next);
        }

        private void RemoveExp(string key, int delta)
        {
            int current = GetExp(key);
            int next = Math.Max(0, current - Math.Max(0, delta));
            UpsertExp(key, next);
        }

        private void OnNpcKilled(NpcKilledEventArgs args)
        {
            if (args.npc == null || args.npc.friendly || args.npc.lifeMax <= 5)
                return;

            if (args.npc.SpawnedFromStatue)
                return;

            foreach (TSPlayer player in TShock.Players)
            {
                if (player == null || !player.Active)
                    continue;

                if (args.npc.lastInteraction == player.Index)
                {
                    string key = GetPlayerKey(player);
                    int expGain = 0;

                    if (args.npc.type == NPCID.Pinky ||
                        args.npc.type == NPCID.Nymph ||
                        args.npc.type == NPCID.LostGirl ||
                        args.npc.type == NPCID.EyeofCthulhu)
                    {
                        expGain = rng.Next(2001, 5001);
                    }
                    else if (args.npc.lifeMax < 200)
                    {
                        expGain = rng.Next(1, 21);
                    }
                    else if (args.npc.lifeMax >= 200 && args.npc.lifeMax < 5000)
                    {
                        expGain = rng.Next(500, 1001);
                    }
                    else if (args.npc.boss || args.npc.lifeMax >= 5000)
                    {
                        expGain = rng.Next(2001, 5001);
                    }

                    if (expGain <= 0)
                        expGain = rng.Next(1, 11);

                    AddExp(key, expGain);
                    int total = GetExp(key);

                    player.SendSuccessMessage($"You gained {expGain} EXP from {args.npc.FullName}! Total EXP: {total}");
                }
            }
        }

        private void MyLevelCommand(CommandArgs args)
        {
            string key = GetPlayerKey(args.Player);
            int exp = GetExp(key);
            args.Player.SendInfoMessage($"Your current EXP: {exp}");
        }

        private void SetLevelCommand(CommandArgs args)
        {
            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /setlevel <player> <exp>");
                return;
            }

            string targetName = args.Parameters[0];
            if (!int.TryParse(args.Parameters[1], out int newExp) || newExp < 0)
            {
                args.Player.SendErrorMessage("EXP must be a non-negative number.");
                return;
            }

            var matches = TSPlayer.FindByNameOrID(targetName);
            if (matches.Count != 1)
            {
                args.Player.SendErrorMessage(matches.Count == 0 ? "No players matched." : "More than one player matched.");
                return;
            }

            TSPlayer target = matches[0];
            string key = GetPlayerKey(target);

            UpsertExp(key, newExp);
            args.Player.SendSuccessMessage($"Set {target.Name}'s EXP to {newExp}.");
            target.SendInfoMessage($"{args.Player.Name} set your EXP to {newExp}.");
        }

        private void AddLevelCommand(CommandArgs args)
        {
            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /addlevel <player> <exp>");
                return;
            }

            string targetName = args.Parameters[0];
            if (!int.TryParse(args.Parameters[1], out int addExp) || addExp <= 0)
            {
                args.Player.SendErrorMessage("EXP must be a positive number.");
                return;
            }

            var matches = TSPlayer.FindByNameOrID(targetName);
            if (matches.Count != 1)
            {
                args.Player.SendErrorMessage(matches.Count == 0 ? "No players matched." : "More than one player matched.");
                return;
            }

            TSPlayer target = matches[0];
            string key = GetPlayerKey(target);

            AddExp(key, addExp);
            int total = GetExp(key);

            args.Player.SendSuccessMessage($"Added {addExp} EXP to {target.Name}. Total: {total}");
            target.SendInfoMessage($"{args.Player.Name} added {addExp} EXP to you. New total: {total}");
        }

        private void MinusLevelCommand(CommandArgs args)
        {
            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /minuslevel <player> <exp>");
                return;
            }

            string targetName = args.Parameters[0];
            if (!int.TryParse(args.Parameters[1], out int minusExp) || minusExp <= 0)
            {
                args.Player.SendErrorMessage("EXP must be a positive number.");
                return;
            }

            var matches = TSPlayer.FindByNameOrID(targetName);
            if (matches.Count != 1)
            {
                args.Player.SendErrorMessage(matches.Count == 0 ? "No players matched." : "More than one player matched.");
                return;
            }

            TSPlayer target = matches[0];
            string key = GetPlayerKey(target);

            RemoveExp(key, minusExp);
            int total = GetExp(key);

            args.Player.SendSuccessMessage($"Removed {minusExp} EXP from {target.Name}. Total: {total}");
            target.SendInfoMessage($"{args.Player.Name} removed {minusExp} EXP from you. New total: {total}");
        }

        private void ResetLevelCommand(CommandArgs args)
        {
            if (args.Parameters.Count < 1)
            {
                args.Player.SendErrorMessage("Usage: /resetlevel <player>");
                return;
            }

            string targetName = args.Parameters[0];
            var matches = TSPlayer.FindByNameOrID(targetName);

            if (matches.Count == 0)
            {
                args.Player.SendErrorMessage("No players matched.");
                return;
            }
            if (matches.Count > 1)
            {
                args.Player.SendErrorMessage("More than one player matched.");
                return;
            }

            TSPlayer target = matches[0];
            string key = GetPlayerKey(target);

            UpsertExp(key, 0);

            args.Player.SendSuccessMessage($"Reset {target.Name}'s EXP to 0.");
            target.SendInfoMessage($"{args.Player.Name} reset your EXP to 0. :(");
        }
    }
}













