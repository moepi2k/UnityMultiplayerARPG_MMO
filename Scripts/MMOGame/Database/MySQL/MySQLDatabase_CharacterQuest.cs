﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MySql.Data.MySqlClient;

namespace Insthync.MMOG
{
    public partial class MySQLDatabase
    {
        private Dictionary<string, int> ReadKillMonsters(string killMonsters)
        {
            var result = new Dictionary<string, int>();
            var splitSets = killMonsters.Split(';');
            foreach (var set in splitSets)
            {
                var splitData = set.Split(':');
                result[splitData[0]] = int.Parse(splitData[1]);
            }
            return result;
        }

        private string WriteKillMonsters(Dictionary<string, int> killMonsters)
        {
            var result = "";
            foreach (var keyValue in killMonsters)
            {
                result += keyValue.Key + ":" + keyValue.Value + ";";
            }
            return result;
        }

        private bool ReadCharacterQuest(MySQLRowsReader reader, out CharacterQuest result, bool resetReader = true)
        {
            if (resetReader)
                reader.ResetReader();

            if (reader.Read())
            {
                result = new CharacterQuest();
                result.questId = reader.GetString("questId");
                result.isComplete = reader.GetBoolean("isComplete");
                result.killedMonsters = ReadKillMonsters(reader.GetString("killMonsters"));
                return true;
            }
            result = CharacterQuest.Empty;
            return false;
        }

        public override void CreateCharacterQuest(string characterId, CharacterQuest characterQuest)
        {
            ExecuteNonQuery("INSERT INTO characterquest (characterId, questId, isComplete, killedMonsters) VALUES (@characterId, @questId, @isComplete, @killedMonsters)",
                new MySqlParameter("@characterId", characterId),
                new MySqlParameter("@questId", characterQuest.questId),
                new MySqlParameter("@isComplete", characterQuest.isComplete),
                new MySqlParameter("@killedMonsters", WriteKillMonsters(characterQuest.killedMonsters)));
        }

        public override CharacterQuest ReadCharacterQuest(string characterId, string questId)
        {
            var reader = ExecuteReader("SELECT * FROM characterquest WHERE characterId=@characterId AND questId=@questId LIMIT 1",
                new MySqlParameter("@characterId", characterId),
                new MySqlParameter("@questId", questId));
            CharacterQuest result;
            ReadCharacterQuest(reader, out result);
            return result;
        }

        public override List<CharacterQuest> ReadCharacterQuests(string characterId)
        {
            var result = new List<CharacterQuest>();
            var reader = ExecuteReader("SELECT * FROM characterquest WHERE characterId=@characterId",
                new MySqlParameter("@characterId", characterId));
            CharacterQuest tempQuest;
            while (ReadCharacterQuest(reader, out tempQuest, false))
            {
                result.Add(tempQuest);
            }
            return result;
        }

        public override void UpdateCharacterQuest(string characterId, CharacterQuest characterQuest)
        {
            ExecuteNonQuery("UPDATE characterquest SET isComplete=@isComplete, killedMonsters=@killedMonsters WHERE characterId=@characterId AND questId=@questId",
                new MySqlParameter("@isComplete", characterQuest.isComplete),
                new MySqlParameter("@killedMonsters", WriteKillMonsters(characterQuest.killedMonsters)),
                new MySqlParameter("@characterId", characterId),
                new MySqlParameter("@questId", characterQuest.questId));
        }

        public override void DeleteCharacterQuest(string characterId, string questId)
        {
            ExecuteNonQuery("DELETE FROM characterquest WHERE characterId=@characterId AND questId=@questId",
                new MySqlParameter("@characterId", characterId),
                new MySqlParameter("@questId", questId));
        }
    }
}