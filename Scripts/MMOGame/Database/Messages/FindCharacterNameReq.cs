﻿using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO
{
    public struct FindCharacterNameReq : INetSerializable
    {
        public string CharacterName { get; set; }
        public int Skip { get; set; }
        public int Limit { get; set; }

        public void Deserialize(NetDataReader reader)
        {
            CharacterName = reader.GetString();
            Skip = reader.GetInt();
            Limit = reader.GetInt();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(CharacterName);
            writer.Put(Skip);
            writer.Put(Limit);
        }
    }
}