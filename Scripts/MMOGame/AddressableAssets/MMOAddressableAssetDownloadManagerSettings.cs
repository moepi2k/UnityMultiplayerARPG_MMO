#if !DISABLE_ADDRESSABLES
using Insthync.AddressableAssetTools;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace MultiplayerARPG.MMO
{
    [CreateAssetMenu(fileName = "MMO Addressable Asset Download Manager Settings", menuName = "Addressables/MMO Addressable Asset Download Manager Settings")]
    public class MMOAddressableAssetDownloadManagerSettings : AddressableAssetDownloadManagerSettings
    {
        public AssetReferenceMapNetworkManager mapNetworkManager;
        public AssetReferenceMMOClientInstance mmoClientInstance;
        public AssetReferenceMMOServerInstance mmoServerInstance;
        public AssetReferenceGameInstance gameInstance;

        [System.NonSerialized]
        private List<AssetReference> _filledInitialObjects = null;
        public override List<AssetReference> InitialObjects
        {
            get
            {
                if (_filledInitialObjects == null)
                {
                    _filledInitialObjects = new List<AssetReference>();
                    _filledInitialObjects.AddRange(initialObjects);
                    _filledInitialObjects.Add(mapNetworkManager);
#if !UNITY_SERVER
                    _filledInitialObjects.Add(mmoClientInstance);
#endif
#if UNITY_STANDALONE && (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES)
                    _filledInitialObjects.Add(mmoServerInstance);
#endif
                    _filledInitialObjects.Add(gameInstance);
                }
                return _filledInitialObjects;
            }
        }
    }
}
#endif
