﻿using UnityEngine;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Cysharp.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Net.Sockets;
using ConcurrentCollections;

namespace MultiplayerARPG.MMO
{
    [DefaultExecutionOrder(DefaultExecutionOrders.MAP_NETWORK_MANAGER)]
    public partial class MapNetworkManager : BaseGameNetworkManager, IAppServer
    {
        public const float TERMINATE_INSTANCE_DELAY = 30f;  // Close instance when no clients connected within 30 seconds

        public struct PendingSpawnPlayerCharacter
        {
            public long connectionId;
            public string userId;
            public string accessToken;
            public string selectCharacterId;
        }

        public struct InstanceMapWarpingLocation
        {
            public string mapName;
            public Vector3 position;
            public bool overrideRotation;
            public Vector3 rotation;
        }

        /// <summary>
        /// If this is not empty it mean this is temporary instance map
        /// So it won't have to save current map, current position to database
        /// </summary>
        public string MapInstanceId { get; set; }
        public Vector3 MapInstanceWarpToPosition { get; set; }
        public bool MapInstanceWarpOverrideRotation { get; set; }
        public Vector3 MapInstanceWarpToRotation { get; set; }

        [Header("Central Network Connection")]
        public string clusterServerAddress = "127.0.0.1";
        public int clusterServerPort = 6010;
        public string machineAddress = "127.0.0.1";

        [Header("Database")]
        public float autoSaveDuration = 2f;

        [Header("Map Spawn")]
        public int mapSpawnMillisecondsTimeout = 0;

        [Header("Player Disconnection")]
        public int playerCharacterDespawnMillisecondsDelay = 5000;

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public IDatabaseClient DatabaseClient
        {
            get { return MMOServerInstance.Singleton.DatabaseClient; }
        }
        public ClusterClient ClusterClient { get; private set; }
#endif

        public bool IsAllocate { get; set; } = false;
        public string ClusterServerAddress { get { return clusterServerAddress; } }
        public int ClusterServerPort { get { return clusterServerPort; } }
        public string AppAddress { get { return machineAddress; } }
        public int AppPort { get { return networkPort; } }
        public string ChannelId { get; set; }
        public string RefId
        {
            get
            {
                if (IsAllocate)
                    return CurrentMapInfo.Id;
                if (IsInstanceMap())
                    return MapInstanceId;
                return CurrentMapInfo.Id;
            }
        }
        public CentralServerPeerType PeerType
        {
            get
            {
                if (IsAllocate)
                    return CentralServerPeerType.AllocateMapServer;
                if (IsInstanceMap())
                    return CentralServerPeerType.InstanceMapServer;
                return CentralServerPeerType.MapServer;
            }
        }
        public bool ProceedingBeforeQuit { get; private set; } = false;
        public bool ReadyToQuit { get; private set; } = false;
        private float _lastSaveTime;
        private float _terminatingTime;
        // Listing
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private readonly ConcurrentDictionary<string, InstanceMapWarpingLocation> _locationsBeforeEnterInstance = new ConcurrentDictionary<string, InstanceMapWarpingLocation>();
        private readonly ConcurrentDictionary<string, CentralServerPeerInfo> _mapServerConnectionIdsBySceneName = new ConcurrentDictionary<string, CentralServerPeerInfo>();
        private readonly ConcurrentDictionary<string, CentralServerPeerInfo> _instanceMapServerConnectionIdsByInstanceId = new ConcurrentDictionary<string, CentralServerPeerInfo>();
        private readonly ConcurrentDictionary<string, SocialCharacterData> _usersByCharacterId = new ConcurrentDictionary<string, SocialCharacterData>();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _despawningPlayerCharacterCancellations = new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, BasePlayerCharacterEntity> _despawningPlayerCharacterEntities = new ConcurrentDictionary<string, BasePlayerCharacterEntity>();
        private readonly ConcurrentDictionary<string, long> _connectionsByUserId = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, string> _accessTokensByUserId = new ConcurrentDictionary<string, string>();
        // Database operations
        private readonly ConcurrentHashSet<StorageId> _loadingStorageIds = new ConcurrentHashSet<StorageId>();
        private readonly ConcurrentHashSet<int> _loadingPartyIds = new ConcurrentHashSet<int>();
        private readonly ConcurrentHashSet<int> _loadingGuildIds = new ConcurrentHashSet<int>();
        internal readonly ConcurrentHashSet<string> savingCharacters = new ConcurrentHashSet<string>();
        internal readonly ConcurrentHashSet<string> savingBuildings = new ConcurrentHashSet<string>();
        internal readonly ConcurrentHashSet<StorageId> pendingSaveStorageIds = new ConcurrentHashSet<StorageId>();
        internal readonly ConcurrentHashSet<string> cancellingReserveStorageCharacterIds = new ConcurrentHashSet<string>();
#endif

        protected override void Awake()
        {
            PrepareMapHandlers();
            base.Awake();
        }

        protected override void Start()
        {
            base.Start();
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            // Cluster client which will be used by map server to connect to cluster server
            ClusterClient = new ClusterClient(this);
            ClusterClient.onResponseAppServerRegister = OnResponseAppServerRegister;
            ClusterClient.onResponseAppServerAddress = OnResponseAppServerAddress;
            ClusterClient.onPlayerCharacterRemoved = OnPlayerCharacterRemoved;
            ClusterClient.onKickUser = KickUser;
            ClusterClient.RegisterResponseHandler<RequestSpawnMapMessage, ResponseSpawnMapMessage>(MMORequestTypes.RequestSpawnMap);
            ClusterClient.RegisterRequestHandler<RequestForceDespawnCharacterMessage, EmptyMessage>(MMORequestTypes.RequestForceDespawnCharacter, HandleRequestForceDespawnCharacter);
            ClusterClient.RegisterRequestHandler<RequestSpawnMapMessage, ResponseSpawnMapMessage>(MMORequestTypes.RequestRunMap, HandleRequestRunMap);
            ClusterClient.RegisterMessageHandler(MMOMessageTypes.Chat, HandleChat);
            ClusterClient.RegisterMessageHandler(MMOMessageTypes.UpdateMapUser, HandleUpdateMapUser);
            ClusterClient.RegisterMessageHandler(MMOMessageTypes.UpdatePartyMember, HandleUpdatePartyMember);
            ClusterClient.RegisterMessageHandler(MMOMessageTypes.UpdateParty, HandleUpdateParty);
            ClusterClient.RegisterMessageHandler(MMOMessageTypes.UpdateGuildMember, HandleUpdateGuildMember);
            ClusterClient.RegisterMessageHandler(MMOMessageTypes.UpdateGuild, HandleUpdateGuild);
#endif
        }

        protected override void Update()
        {
            base.Update();

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            float tempTime = Time.unscaledTime;
            if (IsServer)
            {
                ClusterClient.Update();

                if (IsAllocate)
                {
                    // Stll not running yet, so it won't allow anyone to enter this map-server, so it won't save any data too
                    return;
                }

                if (tempTime - _lastSaveTime > autoSaveDuration)
                {
                    _lastSaveTime = tempTime;
                    SaveAllCharacters().Forget();
                    if (!IsInstanceMap())
                    {
                        // Don't save building if it's instance map
                        SaveAllBuildings().Forget();
                    }
                }

                if (IsInstanceMap())
                {
                    // Quitting application when no players
                    if (Players.Count > 0)
                        _terminatingTime = tempTime;
                    else if (tempTime - _terminatingTime >= TERMINATE_INSTANCE_DELAY)
                        Application.Quit();
                }
            }
#endif
        }

        protected override void Clean()
        {
            base.Clean();
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            _locationsBeforeEnterInstance.Clear();
            _mapServerConnectionIdsBySceneName.Clear();
            _instanceMapServerConnectionIdsByInstanceId.Clear();
            _usersByCharacterId.Clear();
            _loadingStorageIds.Clear();
            _loadingPartyIds.Clear();
            _loadingGuildIds.Clear();
            savingCharacters.Clear();
            savingBuildings.Clear();
            _connectionsByUserId.Clear();
            _accessTokensByUserId.Clear();
#endif
        }

        protected override void UpdateOnlineCharacter(BasePlayerCharacterEntity playerCharacterEntity)
        {
            base.UpdateOnlineCharacter(playerCharacterEntity);
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (ClusterClient.IsNetworkActive && _usersByCharacterId.TryGetValue(playerCharacterEntity.Id, out SocialCharacterData tempUserData))
            {
                _usersByCharacterId[playerCharacterEntity.Id] = tempUserData = SocialCharacterData.Create(playerCharacterEntity);
                UpdateMapUser(ClusterClient, UpdateUserCharacterMessage.UpdateType.Online, tempUserData);
            }
#endif
        }

        public async void ProceedBeforeQuit()
        {
            if (ProceedingBeforeQuit)
                return;
            ProceedingBeforeQuit = true;
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            foreach (BasePlayerCharacterEntity playerCharacter in ServerUserHandlers.GetPlayerCharacters())
            {
                if (playerCharacter == null) continue;
                await DatabaseClient.UpdateCharacterAsync(new UpdateCharacterReq()
                {
                    CharacterData = playerCharacter.CloneTo(new PlayerCharacterData())
                });
            }
            foreach (BuildingEntity buildingEntity in ServerBuildingHandlers.GetBuildings())
            {
                if (buildingEntity == null) continue;
                await DatabaseClient.UpdateBuildingAsync(new UpdateBuildingReq()
                {
                    ChannelId = ChannelId,
                    MapName = CurrentMapInfo.Id,
                    BuildingData = buildingEntity.CloneTo(new BuildingSaveData())
                });
            }
#endif
            await UniTask.Yield();
            ReadyToQuit = true;
            // Request to quit again
            Application.Quit();
        }

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override void RegisterPlayerCharacter(long connectionId, BasePlayerCharacterEntity playerCharacterEntity)
        {
            // Set user data to map server
            if (!_usersByCharacterId.ContainsKey(playerCharacterEntity.Id))
            {
                SocialCharacterData userData = SocialCharacterData.Create(playerCharacterEntity);
                _usersByCharacterId.TryAdd(userData.id, userData);
                // Add map user to cluster server
                if (ClusterClient.IsNetworkActive)
                    UpdateMapUser(ClusterClient, UpdateUserCharacterMessage.UpdateType.Add, userData);
            }
            base.RegisterPlayerCharacter(connectionId, playerCharacterEntity);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override void UnregisterPlayerCharacter(long connectionId)
        {
            // Send remove character from map server
            if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData playerCharacter) &&
                _usersByCharacterId.TryGetValue(playerCharacter.Id, out SocialCharacterData userData))
            {
                _usersByCharacterId.TryRemove(playerCharacter.Id, out _);
                // Remove map user from cluster server
                if (ClusterClient.IsNetworkActive)
                    UpdateMapUser(ClusterClient, UpdateUserCharacterMessage.UpdateType.Remove, userData);
            }
            base.UnregisterPlayerCharacter(connectionId);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public async UniTask SaveAndDestroyDespawningPlayerCharacter(string characterId)
        {
            // Find despawning character
            if (_despawningPlayerCharacterCancellations.TryGetValue(characterId, out CancellationTokenSource cancellationTokenSource) &&
                _despawningPlayerCharacterEntities.TryGetValue(characterId, out BasePlayerCharacterEntity playerCharacterEntity) &&
                !cancellationTokenSource.IsCancellationRequested)
            {
                // Cancel character despawning to despawning immediately
                _despawningPlayerCharacterCancellations.TryRemove(characterId, out _);
                _despawningPlayerCharacterEntities.TryRemove(characterId, out _);
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();

                // Save character before despawned
                await WaitAndSaveCharacter(playerCharacterEntity);

                // Despawn the character
                playerCharacterEntity.NetworkDestroy();
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override void RegisterUserId(long connectionId, string userId)
        {
            base.RegisterUserId(connectionId, userId);
            _connectionsByUserId.TryRemove(userId, out _);
            _connectionsByUserId.TryAdd(userId, connectionId);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public void RegisterUserIdAndAccessToken(long connectionId, string userId, string accessToken)
        {
            RegisterUserId(connectionId, userId);
            _accessTokensByUserId.TryRemove(userId, out _);
            _accessTokensByUserId.TryAdd(userId, accessToken);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override void OnClientDisconnected(DisconnectReason reason, SocketError socketError, byte[] data)
        {
            GameInstance.UserId = string.Empty;
            GameInstance.UserToken = string.Empty;
            base.OnClientDisconnected(reason, socketError, data);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override void UnregisterUserId(long connectionId)
        {
            if (ServerUserHandlers.TryGetUserId(connectionId, out string userId))
            {
                _connectionsByUserId.TryRemove(userId, out _);
                _accessTokensByUserId.TryRemove(userId, out _);
            }
            base.UnregisterUserId(connectionId);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override async void OnPeerDisconnected(long connectionId, DisconnectReason reason, SocketError socketError)
        {
            base.OnPeerDisconnected(connectionId, reason, socketError);
            // Save player character data
            if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out BasePlayerCharacterEntity playerCharacterEntity))
            {
                cancellingReserveStorageCharacterIds.Add(playerCharacterEntity.Id);

                // Clear character states
                playerCharacterEntity.Dealing.StopDealing();
                playerCharacterEntity.Vending.StopVending();
                playerCharacterEntity.SetOwnerClient(-1);
                playerCharacterEntity.StopMove();
                MovementState movementState = playerCharacterEntity.MovementState;
                movementState &= ~MovementState.Forward;
                movementState &= ~MovementState.Backward;
                movementState &= ~MovementState.Right;
                movementState &= ~MovementState.Left;
                playerCharacterEntity.KeyMovement(Vector3.zero, movementState);
                string id = playerCharacterEntity.Id;
                // Store despawning player character id, it will be used later if player not connect and continue playing the character
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                _despawningPlayerCharacterCancellations.TryAdd(id, cancellationTokenSource);
                _despawningPlayerCharacterEntities.TryAdd(id, playerCharacterEntity);

                // Unregister player character
                UnregisterPlayerCharacter(connectionId);
                UnregisterUserId(connectionId);

                // Save character immediately when player disconnect
                await WaitAndSaveCharacter(playerCharacterEntity);

                try
                {
                    if (!IsInstanceMap())
                        await UniTask.Delay(playerCharacterDespawnMillisecondsDelay, true, PlayerLoopTiming.Update, cancellationTokenSource.Token);

                    // Save the characer
                    await WaitAndSaveCharacter(playerCharacterEntity, cancellationTokenSource.Token);

                    // Destroy character from server
                    playerCharacterEntity.NetworkDestroy();
                    _despawningPlayerCharacterEntities.TryRemove(id, out _);
                }
                catch (System.OperationCanceledException)
                {
                    // Catch the cancellation
                }
                catch (System.Exception ex)
                {
                    // Other errors
                    Logging.LogException(LogTag, ex);
                }
                finally
                {
                    if (_despawningPlayerCharacterCancellations.TryRemove(id, out cancellationTokenSource))
                    {
                        try
                        {
                            cancellationTokenSource.Dispose();
                        }
                        catch (System.ObjectDisposedException)
                        {
                            // Already disposed
                        }
                        catch (System.Exception ex)
                        {
                            // Other errors
                            Logging.LogException(LogTag, ex);
                        }
                    }
                }
            }
            else
            {
                UnregisterPlayerCharacter(connectionId);
                UnregisterUserId(connectionId);
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override void OnStopServer()
        {
            base.OnStopServer();
            ClusterClient.OnAppStop();
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override async UniTask PreSpawnEntities()
        {
            // Spawn buildings
            if (!IsAllocate && !IsInstanceMap())
            {
                // Load buildings
                BuildingEntity[] inSceneBuildings = FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None);
                Dictionary<string, BuildingEntity> inSceneBuildingDicts = new Dictionary<string, BuildingEntity>();
                for (int i = 0; i < inSceneBuildings.Length; ++i)
                {
                    inSceneBuildingDicts.Add(inSceneBuildings[i].Id, inSceneBuildings[i]);
                }
                // Don't load buildings if it's instance map
                DatabaseApiResult<BuildingsResp> buildingsResp;
                do
                {
                    buildingsResp = await DatabaseClient.ReadBuildingsAsync(new ReadBuildingsReq()
                    {
                        ChannelId = ChannelId,
                        MapName = CurrentMapInfo.Id,
                    });
                } while (!buildingsResp.IsSuccess);
                HashSet<StorageId> storageIds = new HashSet<StorageId>();
                List<BuildingSaveData> buildings = buildingsResp.Response.List;
                foreach (BuildingSaveData building in buildings)
                {
                    if (building.IsSceneObject && inSceneBuildingDicts.TryGetValue(building.Id, out BuildingEntity inSceneBuilding))
                    {
                        if (building.CurrentHp <= 0)
                        {
                            inSceneBuilding.NetworkDestroy();
                            GameInstance.ServerBuildingHandlers.AddBuilding(building.Id, building);
                            inSceneBuildingDicts.Remove(building.Id);
                            continue;
                        }
                        inSceneBuilding.CurrentHp = building.CurrentHp;
                        GameInstance.ServerBuildingHandlers.AddBuilding(inSceneBuilding.Id, inSceneBuilding);
                        inSceneBuildingDicts.Remove(building.Id);
                        if (inSceneBuilding is StorageEntity)
                            storageIds.Add(new StorageId(StorageType.Building, inSceneBuilding.Id));
                    }
                    else
                    {
                        BuildingEntity buildingEntity = CreateBuildingEntity(building, true);
                        if (buildingEntity is StorageEntity)
                            storageIds.Add(new StorageId(StorageType.Building, buildingEntity.Id));
                    }
                }
                // Setup building
                foreach (BuildingEntity inSceneBuilding in inSceneBuildingDicts.Values)
                {
                    inSceneBuilding.InitSceneObject();
                    GameInstance.ServerBuildingHandlers.AddBuilding(inSceneBuilding.Id, inSceneBuilding);
                }
                List<UniTask> tasks = new List<UniTask>();
                // Load building storage
                foreach (StorageId storageId in storageIds)
                {
                    tasks.Add(LoadStorageRoutine(storageId));
                }
                // Wait until all building storage loaded
                await UniTask.WhenAll(tasks);
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override UniTask PostSpawnEntities()
        {
            ClusterClient.OnAppStart();
            return UniTask.CompletedTask;
        }
#endif

        #region Character spawn function
        public override void SerializeEnterGameData(NetDataWriter writer)
        {
            writer.Put(GameInstance.UserId);
            writer.Put(GameInstance.UserToken);
            writer.Put(GameInstance.SelectedCharacterId);
        }

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override async UniTask<bool> DeserializeEnterGameData(long connectionId, NetDataReader reader)
        {
            if (IsAllocate)
            {
                return false;
            }

            string userId = reader.GetString();
            string accessToken = reader.GetString();
            string selectCharacterId = reader.GetString();
            if (!await ValidatePlayerConnection(connectionId, userId, accessToken, selectCharacterId))
            {
                return false;
            }

            await SaveAndDestroyDespawningPlayerCharacter(selectCharacterId);
            // Unregister player
            UnregisterPlayerCharacter(connectionId);
            UnregisterUserId(connectionId);

            return true;
        }
#endif

        protected override void HandleEnterGameResponse(ResponseHandlerData responseHandler, AckResponseCode responseCode, EnterGameResponseMessage response)
        {
            base.HandleEnterGameResponse(responseHandler, responseCode, response);
            if (responseCode == AckResponseCode.Success)
            {
                // Disconnect from central server when accepted by map server
                MMOClientInstance.Singleton.StopCentralClient();
            }
        }

        public override void SerializeClientReadyData(NetDataWriter writer)
        {
            writer.Put(GameInstance.UserId);
            writer.Put(GameInstance.UserToken);
            writer.Put(GameInstance.SelectedCharacterId);
        }

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public override async UniTask<bool> DeserializeClientReadyData(LiteNetLibIdentity playerIdentity, long connectionId, NetDataReader reader)
        {
            if (IsAllocate)
            {
                return false;
            }

            string userId = reader.GetString();
            string accessToken = reader.GetString();
            string selectCharacterId = reader.GetString();
            if (!await ValidatePlayerConnection(connectionId, userId, accessToken, selectCharacterId))
            {
                return false;
            }

            RegisterUserIdAndAccessToken(connectionId, userId, accessToken);
            SetPlayerReadyRoutine(connectionId, userId, accessToken, selectCharacterId).Forget();
            return true;
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private async UniTask<bool> ValidatePlayerConnection(long connectionId, string userId, string accessToken, string selectCharacterId)
        {
            if (!IsServerReadyToInstantiateObjects())
            {
                if (LogError)
                    Logging.LogError(LogTag, "Not ready to spawn player: " + userId + ", user must see map server is not ready, how can this guy reach here? :P.");
                return false;
            }

            if (_connectionsByUserId.ContainsKey(userId))
            {
                if (LogError)
                    Logging.LogError(LogTag, "User trying to hack?: " + userId + ", connection already registered.");
                return false;
            }

            if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out _))
            {
                if (LogError)
                    Logging.LogError(LogTag, "User trying to hack?: " + userId + ", character already registered.");
                return false;
            }

            DatabaseApiResult<ValidateAccessTokenResp> validateAccessTokenResp = await DatabaseClient.ValidateAccessTokenAsync(new ValidateAccessTokenReq()
            {
                UserId = userId,
                AccessToken = accessToken,
            });

            if (!validateAccessTokenResp.IsSuccess || !validateAccessTokenResp.Response.IsPass)
            {
                if (LogError)
                    Logging.LogError(LogTag, "Invalid access token for user: " + userId);
                return false;
            }

            return true;
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private async UniTaskVoid SetPlayerReadyRoutine(long connectionId, string userId, string accessToken, string selectCharacterId)
        {
            DatabaseApiResult<CharacterResp> characterResp = await DatabaseClient.ReadCharacterAsync(new ReadCharacterReq()
            {
                UserId = userId,
                CharacterId = selectCharacterId
            });
            if (!characterResp.IsSuccess)
            {
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }
            PlayerCharacterData playerCharacterData = characterResp.Response.CharacterData;
            // If data is empty / cannot find character, kick the player
            if (playerCharacterData == null)
            {
                if (LogError)
                    Logging.LogError(LogTag, "Cannot find select character: " + selectCharacterId + " for user: " + userId);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }
            // If it is not allow this character data, kick the player
            if (!playerCharacterData.TryGetEntityAddressablePrefab(out _) && !playerCharacterData.TryGetEntityPrefab(out _))
            {
                if (LogError)
                    Logging.LogError(LogTag, "Cannot find player character with entity Id: " + playerCharacterData.EntityId);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }

            // Prepare saving location for this character
            string savingCurrentMapName = playerCharacterData.CurrentMapName;
            Vector3 savingCurrentPosition = playerCharacterData.CurrentPosition;
            Vector3 savingCurrentRotation = playerCharacterData.CurrentRotation;

            if (IsInstanceMap())
            {
                playerCharacterData.CurrentPosition = MapInstanceWarpToPosition;
                if (MapInstanceWarpOverrideRotation)
                    playerCharacterData.CurrentRotation = MapInstanceWarpToRotation;
            }

            // Set proper spawn position
            CurrentMapInfo.GetEnterMapPoint(playerCharacterData, out string mapName, out Vector3 position, out Vector3 rotation);
            playerCharacterData.CurrentMapName = mapName;
            playerCharacterData.CurrentPosition = position;
            playerCharacterData.CurrentRotation = rotation;

            // Spawn character entity and set its data
            Quaternion characterRotation = Quaternion.identity;
            if (CurrentGameInstance.DimensionType == DimensionType.Dimension3D)
                characterRotation = Quaternion.Euler(playerCharacterData.CurrentRotation);
            // NOTE: entity ID is a hash asset ID :)
            LiteNetLibIdentity spawnObj = Assets.GetObjectInstance(
                playerCharacterData.EntityId,
                playerCharacterData.CurrentPosition,
                characterRotation);

            // Set current character data
            BasePlayerCharacterEntity playerCharacterEntity = spawnObj.GetComponent<BasePlayerCharacterEntity>();
            SetLocationBeforeEnterInstance(playerCharacterData.Id, savingCurrentMapName, savingCurrentPosition, savingCurrentRotation);
            playerCharacterData.CloneTo(playerCharacterEntity);

            // Set currencies
            // Gold
            DatabaseApiResult<GoldResp> getGoldResp = await DatabaseClient.GetGoldAsync(new GetGoldReq()
            {
                UserId = userId,
            });
            if (!getGoldResp.IsSuccess)
            {
                Destroy(spawnObj.gameObject);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }
            playerCharacterEntity.UserGold = getGoldResp.Response.Gold;
            // Cash
            DatabaseApiResult<CashResp> getCashResp = await DatabaseClient.GetCashAsync(new GetCashReq()
            {
                UserId = userId,
            });
            if (!getCashResp.IsSuccess)
            {
                Destroy(spawnObj.gameObject);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }
            playerCharacterEntity.UserCash = getCashResp.Response.Cash;

            // Set user Id
            playerCharacterEntity.UserId = userId;

            // Load user level
            DatabaseApiResult<GetUserLevelResp> getUserLevelResp = await DatabaseClient.GetUserLevelAsync(new GetUserLevelReq()
            {
                UserId = userId,
                AccessToken = accessToken,
            });
            if (!getUserLevelResp.IsSuccess)
            {
                Destroy(spawnObj.gameObject);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }
            playerCharacterEntity.UserLevel = getUserLevelResp.Response.UserLevel;

            // Load party data, if this map-server does not have party data
            if (playerCharacterEntity.PartyId > 0)
            {
                if (!ServerPartyHandlers.ContainsParty(playerCharacterEntity.PartyId))
                    await LoadPartyRoutine(playerCharacterEntity.PartyId);
                if (ServerPartyHandlers.TryGetParty(playerCharacterEntity.PartyId, out PartyData party))
                {
                    ServerGameMessageHandlers.SendSetFullPartyData(connectionId, party);
                }
                else
                {
                    playerCharacterEntity.ClearParty();
                }
            }

            // Load guild data, if this map-server does not have guild data
            if (playerCharacterEntity.GuildId > 0)
            {
                if (!ServerGuildHandlers.ContainsGuild(playerCharacterEntity.GuildId))
                    await LoadGuildRoutine(playerCharacterEntity.GuildId);
                if (ServerGuildHandlers.TryGetGuild(playerCharacterEntity.GuildId, out GuildData guild))
                {
                    playerCharacterEntity.GuildRole = guild.GetMemberRole(playerCharacterEntity.Id);
                    ServerGameMessageHandlers.SendSetFullGuildData(connectionId, guild);
                }
                else
                {
                    playerCharacterEntity.ClearGuild();
                }
            }

            // Load storage
            StorageId storageId = new StorageId(StorageType.Player, userId);
            await LoadStorageRoutine(storageId);

            // Force make caches, to calculate current stats to fill empty slots items
            playerCharacterEntity.ForceMakeCaches();
            playerCharacterEntity.FillEmptySlots();

            List<CharacterBuff> summonBuffs = new List<CharacterBuff>();
            if (!playerCharacterEntity.IsDead())
            {
                // Summon saved summons
                DatabaseApiResult<GetSummonBuffsResp> summonBuffsResp = await DatabaseClient.GetSummonBuffsAsync(new GetSummonBuffsReq()
                {
                    CharacterId = playerCharacterEntity.Id,
                });
                if (!summonBuffsResp.IsSuccess)
                {
                    Destroy(spawnObj.gameObject);
                    KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                    return;
                }
                summonBuffs.AddRange(summonBuffsResp.Response.SummonBuffs);
            }

            // Make sure that player does not exit before character data loaded
            if (!ContainsConnectionId(connectionId))
            {
                Destroy(spawnObj.gameObject);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }

            // Make sure that there is no another player, enter the game with the character yet (prevent nested login)
            if (_usersByCharacterId.ContainsKey(playerCharacterEntity.Id))
            {
                Destroy(spawnObj.gameObject);
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                return;
            }

            // Spawn the character
            Assets.NetworkSpawn(spawnObj, 0, connectionId);

            // Register player character entity to the server
            RegisterPlayerCharacter(connectionId, playerCharacterEntity);

            // Don't destroy player character entity when disconnect
            playerCharacterEntity.Identity.DoNotDestroyWhenDisconnect = true;

            // Notify clients that this character is spawn or dead
            if (!playerCharacterEntity.IsDead())
            {
                playerCharacterEntity.CallRpcOnRespawn();
                // Summon saved mount entity
                if (GameInstance.AddressableVehicleEntities.TryGetValue(playerCharacterData.MountDataId, out AssetReferenceVehicleEntity addressablePrefab))
                {
                    playerCharacterEntity.Mount(null, addressablePrefab);
                }
                else if (GameInstance.VehicleEntities.TryGetValue(playerCharacterData.MountDataId, out VehicleEntity prefab))
                {
                    playerCharacterEntity.Mount(prefab, null);
                }
                // Summon monsters
                for (int i = 0; i < playerCharacterEntity.Summons.Count; ++i)
                {
                    CharacterSummon summon = playerCharacterEntity.Summons[i];
                    summon.Summon(playerCharacterEntity, summon.level, summon.summonRemainsDuration, summon.exp, summon.currentHp, summon.currentMp);
                    for (int j = 0; j < summonBuffs.Count; ++j)
                    {
                        if (summonBuffs[j].id.StartsWith(i.ToString()))
                        {
                            summon.CacheEntity.Buffs.Add(summonBuffs[j]);
                            summonBuffs.RemoveAt(j);
                            j--;
                        }
                    }
                    playerCharacterEntity.Summons[i] = summon;
                }
            }
            else
            {
                playerCharacterEntity.CallRpcOnDead();
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        public void SetLocationBeforeEnterInstance(string id, string currentMapName, Vector3 currentPosition, Vector3 currentRotation)
        {
            if (!IsInstanceMap())
                return;
            _locationsBeforeEnterInstance.TryRemove(id, out _);
            _locationsBeforeEnterInstance.TryAdd(id, new InstanceMapWarpingLocation()
            {
                mapName = currentMapName,
                position = currentPosition,
                overrideRotation = true,
                rotation = currentRotation,
            });
        }
#endif

        #endregion

        #region Network message handlers
        protected override void HandleWarpAtClient(MessageHandlerData messageHandler)
        {
            MMOWarpMessage message = messageHandler.ReadMessage<MMOWarpMessage>();
            bool tempLoadOfflineSceneOnClientStop = loadOfflineSceneWhenClientStopped;
            loadOfflineSceneWhenClientStopped = false;
            StopClient();
            StartClient(message.networkAddress, message.networkPort);
            loadOfflineSceneWhenClientStopped = tempLoadOfflineSceneOnClientStop;
        }

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override void HandleChatAtServer(MessageHandlerData messageHandler)
        {
            ChatMessage message = messageHandler.ReadMessage<ChatMessage>().FillChannelId();
            string userId = string.Empty;
            string accessToken = string.Empty;
            if (messageHandler.ConnectionId >= 0 && message.sendByServer)
            {
                // This message should be sent by server but its connection >= 0, which means it is not a server ;)
                return;
            }
            // Get character
            IPlayerCharacterData playerCharacter;
            if (!message.sendByServer && !ServerUserHandlers.TryGetPlayerCharacter(messageHandler.ConnectionId, out playerCharacter))
            {
                // Not allow to enter chat
                return;
            }
            else
            {
                // Try to get character by sender name because this chat was sent by server, don't use connection ID to get character
                // But still continue to enter chat if there is no character found (because it is server)
                ServerUserHandlers.TryGetPlayerCharacterByName(message.senderName, out playerCharacter);
            }
            // Setup some data if it can find a character
            if (playerCharacter != null)
            {
                message.senderId = playerCharacter.Id;
                message.senderUserId = playerCharacter.UserId;
                message.senderName = playerCharacter.CharacterName;
                // Set user ID and access token, they will be used by cluster server to validate player
                userId = playerCharacter.UserId;
                if (!_accessTokensByUserId.TryGetValue(userId, out accessToken))
                    accessToken = string.Empty;
                // Set guild data
                if (ServerGuildHandlers.TryGetGuild(playerCharacter.GuildId, out GuildData guildData))
                {
                    message.guildId = playerCharacter.GuildId;
                    message.guildName = guildData.guildName;
                }
            }
            // Character muted?
            if (!message.sendByServer && playerCharacter != null && playerCharacter.IsMuting())
            {
                ServerSendPacket(messageHandler.ConnectionId, 0, DeliveryMethod.ReliableOrdered, GameNetworkingConsts.Chat, new ChatMessage()
                {
                    channel = ChatChannel.System,
                    message = "You have been muted.",
                });
                return;
            }
            // Local chat will processes immediately, not have to be sent to cluster server except some GM commands
            if (message.channel == ChatChannel.Local)
            {
                bool sentGmCommand = false;
                if (message.sendByServer || playerCharacter != null)
                {
                    if (message.sendByServer || (playerCharacter is BasePlayerCharacterEntity playerCharacterEntity &&
                        CurrentGameInstance.GMCommands.IsGMCommand(message.message, out string gmCommand) &&
                        CurrentGameInstance.GMCommands.CanUseGMCommand(playerCharacterEntity.UserLevel, gmCommand)))
                    {
                        // If it's GM command and senderName's user level > 0, handle gm commands
                        // Send GM command to cluster server to broadcast to other servers later
                        if (ClusterClient.IsNetworkActive)
                        {
                            ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MMOMessageTypes.Chat, (writer) =>
                            {
                                writer.PutValue(message);
                                writer.Put(userId);
                                writer.Put(accessToken);
                            });
                        }
                        sentGmCommand = true;
                    }
                }
                if (!sentGmCommand)
                {
                    ServerChatHandlers.OnChatMessage(message);
                    ServerLogHandlers.LogEnterChat(message);
                }
                return;
            }
            // Chat messages for other chat channels will be sent to cluster server, then cluster server will pass it to other map-servers to send to clients
            if (message.channel == ChatChannel.System)
            {
                if (ServerChatHandlers.CanSendSystemAnnounce(message.senderName))
                {
                    if (ClusterClient.IsNetworkActive)
                    {
                        ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MMOMessageTypes.Chat, (writer) =>
                        {
                            writer.PutValue(message);
                            writer.Put(userId);
                            writer.Put(accessToken);
                        });
                    }
                }
                return;
            }
            if (ClusterClient.IsNetworkActive)
            {
                ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MMOMessageTypes.Chat, (writer) =>
                {
                    writer.PutValue(message);
                    writer.Put(userId);
                    writer.Put(accessToken);
                });
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void OnResponseAppServerRegister(AckResponseCode responseCode)
        {
            if (responseCode != AckResponseCode.Success)
                return;
            UpdateMapUsers(ClusterClient, UpdateUserCharacterMessage.UpdateType.Add);
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void OnResponseAppServerAddress(AckResponseCode responseCode, CentralServerPeerInfo peerInfo)
        {
            if (responseCode != AckResponseCode.Success)
                return;
            switch (peerInfo.peerType)
            {
                case CentralServerPeerType.MapServer:
                    if (!string.IsNullOrEmpty(peerInfo.channelId) && !string.IsNullOrEmpty(peerInfo.refId))
                    {
                        string key = peerInfo.GetPeerInfoKey();
                        if (LogInfo)
                            Logging.Log(LogTag, "Register map server: " + key);
                        _mapServerConnectionIdsBySceneName[key] = peerInfo;
                    }
                    break;
                case CentralServerPeerType.InstanceMapServer:
                    if (!string.IsNullOrEmpty(peerInfo.channelId) && !string.IsNullOrEmpty(peerInfo.refId))
                    {
                        string key = peerInfo.GetPeerInfoKey();
                        if (LogInfo)
                            Logging.Log(LogTag, "Register instance map server: " + key);
                        _instanceMapServerConnectionIdsByInstanceId[key] = peerInfo;
                    }
                    break;
            }
        }
#endif

#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private async void OnPlayerCharacterRemoved(string userId, string characterId)
        {
            await SaveAndDestroyDespawningPlayerCharacter(characterId);
        }
#endif
#endregion

        #region Request from central server handlers
        internal async UniTaskVoid HandleRequestForceDespawnCharacter(
            RequestHandlerData requestHandler,
            RequestForceDespawnCharacterMessage request,
            RequestProceedResultDelegate<EmptyMessage> result)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!string.IsNullOrEmpty(request.characterId))
                await SaveAndDestroyDespawningPlayerCharacter(request.characterId);
            // Always success, because it is just despawning player character, if it not found then it still can be determined that it was despawned
            result.InvokeSuccess(EmptyMessage.Value);
#endif
        }

        internal UniTaskVoid HandleRequestRunMap(
            RequestHandlerData requestHandler,
            RequestSpawnMapMessage request,
            RequestProceedResultDelegate<ResponseSpawnMapMessage> result)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (!IsAllocate)
            {
                result.InvokeError(new ResponseSpawnMapMessage()
                {
                    message = UITextKeys.UI_ERROR_APP_NOT_READY,
                });
                return default;
            }

            if (CurrentMapInfo == null || !string.Equals(CurrentMapInfo.Id, request.mapName))
            {
                result.InvokeError(new ResponseSpawnMapMessage()
                {
                    message = UITextKeys.UI_ERROR_INVALID_DATA,
                });
                return default;
            }

            IsAllocate = false;
            ChannelId = request.channelId;
            MapInstanceId = request.instanceId;
            MapInstanceWarpToPosition = request.instanceWarpPosition;
            MapInstanceWarpOverrideRotation = request.instanceWarpOverrideRotation;
            MapInstanceWarpToRotation = request.instanceWarpRotation;
            _terminatingTime = Time.unscaledTime;

            CentralServerPeerInfo peerInfo = new CentralServerPeerInfo()
            {
                peerType = PeerType,
                networkAddress = AppAddress,
                networkPort = AppPort,
                channelId = ChannelId,
                refId = RefId,
            };
            ClusterClient.RequestAppServerRegister(peerInfo);
            result.InvokeSuccess(new ResponseSpawnMapMessage()
            {
                peerInfo = peerInfo,
            });
#endif
            return default;
        }
        #endregion

        #region Social message handlers
        internal async void HandleChat(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            ChatMessage message = messageHandler.ReadMessage<ChatMessage>();
            if (message.channel == ChatChannel.Local)
            {
                // Handle GM command here, only GM command will be broadcasted as local channel
                if (!CurrentGameInstance.GMCommands.IsGMCommand(message.message, out string gmCommand))
                {
                    // Handle only GM command, if it is not GM command then skip
                    return;
                }

                // Get user level from server to validate user level
                DatabaseApiResult<GetUserLevelResp> resp = await DatabaseClient.GetUserLevelAsync(new GetUserLevelReq()
                {
                    UserId = messageHandler.Reader.GetString(),
                    AccessToken = messageHandler.Reader.GetString(),
                });
                if (!resp.IsSuccess)
                {
                    // Error occuring when retreiving user level
                    return;
                }
                if (!CurrentGameInstance.GMCommands.CanUseGMCommand(resp.Response.UserLevel, gmCommand))
                {
                    // User not able to use this command
                    return;
                }
                // Response enter GM command message
                if (!ServerUserHandlers.TryGetPlayerCharacterByName(message.senderName, out BasePlayerCharacterEntity playerCharacterEntity))
                    playerCharacterEntity = null;
                string response = CurrentGameInstance.GMCommands.HandleGMCommand(message.senderName, playerCharacterEntity, message.message);
                if (playerCharacterEntity != null && !string.IsNullOrEmpty(response))
                {
                    ServerSendPacket(playerCharacterEntity.ConnectionId, 0, DeliveryMethod.ReliableOrdered, GameNetworkingConsts.Chat, new ChatMessage()
                    {
                        channel = ChatChannel.System,
                        message = response,
                    });
                }
            }
            else
            {
                ServerChatHandlers.OnChatMessage(message);
                ServerLogHandlers.LogEnterChat(message);
            }
#endif
        }

        internal void HandleUpdateMapUser(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            UpdateUserCharacterMessage message = messageHandler.ReadMessage<UpdateUserCharacterMessage>();
            switch (message.type)
            {
                case UpdateUserCharacterMessage.UpdateType.Add:
                    if (!_usersByCharacterId.ContainsKey(message.character.id))
                        _usersByCharacterId.TryAdd(message.character.id, message.character);
                    break;
                case UpdateUserCharacterMessage.UpdateType.Remove:
                    _usersByCharacterId.TryRemove(message.character.id, out _);
                    break;
                case UpdateUserCharacterMessage.UpdateType.Online:
                    if (_usersByCharacterId.ContainsKey(message.character.id))
                    {
                        int socialId;
                        ServerCharacterHandlers.MarkOnlineCharacter(message.character.id);
                        socialId = message.character.partyId;
                        if (socialId > 0 && ServerPartyHandlers.TryGetParty(socialId, out PartyData party))
                        {
                            party.UpdateMember(message.character);
                            ServerPartyHandlers.SetParty(socialId, party);
                        }
                        socialId = message.character.guildId;
                        if (socialId > 0 && ServerGuildHandlers.TryGetGuild(socialId, out GuildData guild))
                        {
                            guild.UpdateMember(message.character);
                            ServerGuildHandlers.SetGuild(socialId, guild);
                        }
                        _usersByCharacterId[message.character.id] = message.character;
                    }
                    break;
            }
#endif
        }

        internal void HandleUpdatePartyMember(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            UpdateSocialMemberMessage message = messageHandler.ReadMessage<UpdateSocialMemberMessage>();
            BasePlayerCharacterEntity playerCharacterEntity;
            if (ServerPartyHandlers.TryGetParty(message.socialId, out PartyData party) && party.UpdateSocialGroupMember(message))
            {
                switch (message.type)
                {
                    case UpdateSocialMemberMessage.UpdateType.Add:
                        if (ServerUserHandlers.TryGetPlayerCharacterById(message.character.id, out playerCharacterEntity))
                        {
                            playerCharacterEntity.PartyId = message.socialId;
                            ServerGameMessageHandlers.SendSetPartyData(playerCharacterEntity.ConnectionId, party);
                            ServerGameMessageHandlers.SendAddPartyMembersToOne(playerCharacterEntity.ConnectionId, party);
                        }
                        ServerGameMessageHandlers.SendAddPartyMemberToMembers(party, message.character);
                        break;
                    case UpdateSocialMemberMessage.UpdateType.Remove:
                        if (ServerUserHandlers.TryGetPlayerCharacterById(message.character.id, out playerCharacterEntity))
                        {
                            playerCharacterEntity.ClearParty();
                            ServerGameMessageHandlers.SendClearPartyData(playerCharacterEntity.ConnectionId, message.socialId);
                        }
                        ServerGameMessageHandlers.SendRemovePartyMemberToMembers(party, message.character.id);
                        break;
                }
            }
#endif
        }

        internal void HandleUpdateParty(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            UpdatePartyMessage message = messageHandler.ReadMessage<UpdatePartyMessage>();
            BasePlayerCharacterEntity playerCharacterEntity;
            if (ServerPartyHandlers.TryGetParty(message.id, out PartyData party))
            {
                switch (message.type)
                {
                    case UpdatePartyMessage.UpdateType.ChangeLeader:
                        party.SetLeader(message.characterId);
                        ServerPartyHandlers.SetParty(message.id, party);
                        ServerGameMessageHandlers.SendSetPartyLeaderToMembers(party);
                        break;
                    case UpdatePartyMessage.UpdateType.Setting:
                        party.Setting(message.shareExp, message.shareItem);
                        ServerPartyHandlers.SetParty(message.id, party);
                        ServerGameMessageHandlers.SendSetPartySettingToMembers(party);
                        break;
                    case UpdatePartyMessage.UpdateType.Terminate:
                        foreach (string memberId in party.GetMemberIds())
                        {
                            if (ServerUserHandlers.TryGetPlayerCharacterById(memberId, out playerCharacterEntity))
                            {
                                playerCharacterEntity.ClearParty();
                                ServerGameMessageHandlers.SendClearPartyData(playerCharacterEntity.ConnectionId, message.id);
                            }
                        }
                        ServerPartyHandlers.RemoveParty(message.id);
                        break;
                }
            }
#endif
        }

        internal void HandleUpdateGuildMember(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            UpdateSocialMemberMessage message = messageHandler.ReadMessage<UpdateSocialMemberMessage>();
            BasePlayerCharacterEntity playerCharacterEntity;
            if (ServerGuildHandlers.TryGetGuild(message.socialId, out GuildData guild) && guild.UpdateSocialGroupMember(message))
            {
                switch (message.type)
                {
                    case UpdateSocialMemberMessage.UpdateType.Add:
                        if (ServerUserHandlers.TryGetPlayerCharacterById(message.character.id, out playerCharacterEntity))
                        {
                            playerCharacterEntity.GuildId = message.socialId;
                            playerCharacterEntity.GuildRole = guild.GetMemberRole(playerCharacterEntity.Id);
                            ServerGameMessageHandlers.SendSetGuildData(playerCharacterEntity.ConnectionId, guild);
                            ServerGameMessageHandlers.SendAddGuildMembersToOne(playerCharacterEntity.ConnectionId, guild);
                        }
                        ServerGameMessageHandlers.SendAddGuildMemberToMembers(guild, message.character);
                        break;
                    case UpdateSocialMemberMessage.UpdateType.Remove:
                        if (ServerUserHandlers.TryGetPlayerCharacterById(message.character.id, out playerCharacterEntity))
                        {
                            playerCharacterEntity.ClearGuild();
                            ServerGameMessageHandlers.SendClearGuildData(playerCharacterEntity.ConnectionId, message.socialId);
                        }
                        ServerGameMessageHandlers.SendRemoveGuildMemberToMembers(guild, message.character.id);
                        break;
                }
            }
#endif
        }

        internal void HandleUpdateGuild(MessageHandlerData messageHandler)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            UpdateGuildMessage message = messageHandler.ReadMessage<UpdateGuildMessage>();
            BasePlayerCharacterEntity playerCharacterEntity;
            if (ServerGuildHandlers.TryGetGuild(message.id, out GuildData guild))
            {
                switch (message.type)
                {
                    case UpdateGuildMessage.UpdateType.ChangeLeader:
                        guild.SetLeader(message.characterId);
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        if (ServerUserHandlers.TryGetPlayerCharacterById(message.characterId, out playerCharacterEntity))
                            playerCharacterEntity.GuildRole = guild.GetMemberRole(playerCharacterEntity.Id);
                        ServerGameMessageHandlers.SendSetGuildLeaderToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetGuildMessage:
                        guild.guildMessage = message.guildMessage;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildMessageToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetGuildMessage2:
                        guild.guildMessage2 = message.guildMessage;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildMessageToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetGuildRole:
                        guild.SetRole(message.guildRole, message.guildRoleData);
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        foreach (string memberId in guild.GetMemberIds())
                        {
                            if (ServerUserHandlers.TryGetPlayerCharacterById(memberId, out playerCharacterEntity))
                                playerCharacterEntity.GuildRole = guild.GetMemberRole(playerCharacterEntity.Id);
                        }
                        ServerGameMessageHandlers.SendSetGuildRoleToMembers(guild, message.guildRole, message.guildRoleData);
                        break;
                    case UpdateGuildMessage.UpdateType.SetGuildMemberRole:
                        guild.SetMemberRole(message.characterId, message.guildRole);
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        if (ServerUserHandlers.TryGetPlayerCharacterById(message.characterId, out playerCharacterEntity))
                            playerCharacterEntity.GuildRole = guild.GetMemberRole(playerCharacterEntity.Id);
                        ServerGameMessageHandlers.SendSetGuildMemberRoleToMembers(guild, message.characterId, message.guildRole);
                        break;
                    case UpdateGuildMessage.UpdateType.SetSkillLevel:
                        guild.SetSkillLevel(message.dataId, message.level);
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildSkillLevelToMembers(guild, message.dataId);
                        break;
                    case UpdateGuildMessage.UpdateType.SetGold:
                        guild.gold = message.gold;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildGoldToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetScore:
                        guild.score = message.score;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildScoreToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetOptions:
                        guild.options = message.options;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildOptionsToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetAutoAcceptRequests:
                        guild.autoAcceptRequests = message.autoAcceptRequests;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildAutoAcceptRequestsToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.SetRank:
                        guild.rank = message.rank;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildRankToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.LevelExpSkillPoint:
                        guild.level = message.level;
                        guild.exp = message.exp;
                        guild.skillPoint = message.skillPoint;
                        ServerGuildHandlers.SetGuild(message.id, guild);
                        ServerGameMessageHandlers.SendSetGuildLevelExpSkillPointToMembers(guild);
                        break;
                    case UpdateGuildMessage.UpdateType.Terminate:
                        foreach (string memberId in guild.GetMemberIds())
                        {
                            if (ServerUserHandlers.TryGetPlayerCharacterById(memberId, out playerCharacterEntity))
                            {
                                playerCharacterEntity.ClearGuild();
                                ServerGameMessageHandlers.SendClearGuildData(playerCharacterEntity.ConnectionId, message.id);
                            }
                        }
                        ServerGuildHandlers.RemoveGuild(message.id);
                        break;
                }
            }
#endif
        }
        #endregion

        #region Update map user functions
        private void UpdateMapUsers(LiteNetLibClient transportHandler, UpdateUserCharacterMessage.UpdateType updateType)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            foreach (SocialCharacterData user in _usersByCharacterId.Values)
            {
                UpdateMapUser(transportHandler, updateType, user);
            }
#endif
        }

        private void UpdateMapUser(LiteNetLibClient transportHandler, UpdateUserCharacterMessage.UpdateType updateType, SocialCharacterData userData)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            UpdateUserCharacterMessage updateMapUserMessage = new UpdateUserCharacterMessage()
            {
                type = updateType,
                character = userData,
            };
            transportHandler.SendPacket(0, DeliveryMethod.ReliableOrdered, MMOMessageTypes.UpdateMapUser, updateMapUserMessage.Serialize);
#endif
        }
        #endregion

        public void KickUser(string userId, UITextKeys message)
        {
#if (UNITY_EDITOR || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
            if (_connectionsByUserId.TryGetValue(userId, out long connectionId))
                KickClient(connectionId, message);
#endif
        }
    }
}
