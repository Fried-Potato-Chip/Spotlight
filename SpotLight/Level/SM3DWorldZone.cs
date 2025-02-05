﻿using BYAML;
using GL_EditorFramework;
using GL_EditorFramework.GL_Core;
using GL_EditorFramework.Interfaces;
using OpenTK;
using Spotlight.EditorDrawables;
using Syroot.BinaryData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SZS;
using static BYAML.ByamlIterator;
using static GL_EditorFramework.EditorDrawables.EditorSceneBase;

namespace Spotlight.Level
{
    public class ObjectList : List<I3dWorldObject>
    {

    }

    public class ZoneRenderBatch
    {
        Dictionary<Type, IBatchRenderer> batchRenderers = new Dictionary<Type, IBatchRenderer>();

        public void Clear() => batchRenderers.Clear();

        public IBatchRenderer GetBatchRenderer(Type batchType)
        {
            if (!batchRenderers.ContainsKey(batchType))
                batchRenderers.Add(batchType, (IBatchRenderer)Activator.CreateInstance(batchType));

            return batchRenderers[batchType];
        }

        public IEnumerable<IBatchRenderer> BatchRenderers => batchRenderers.Values;
    }

    public interface IBatchRenderer
    {
        void Draw(GL_ControlModern control, Pass pass, Vector4 highlightColor, Matrix4 zoneTransform, Vector4 pickingColor);
    }

    public class SM3DWorldZone
    {
        public override string ToString() => StageName;



        #region Constants
        public const string COMBINED_SUFFIX = ".szs"; //Used in the Captain Toad Treasure Tracker Update and Bowsers Fury

#if ODYSSEY
        public const string MAP_SUFFIX = "Map.szs";
        public const string DESIGN_SUFFIX = "Design.szs";
        public const string SOUND_SUFFIX = "Sound.szs";

        public const string COMMON_SUFFIX = ".szs";
#else
        public const string MAP_SUFFIX = "Map1.szs";
        public const string DESIGN_SUFFIX = "Design1.szs";
        public const string SOUND_SUFFIX = "Sound1.szs";

        public const string COMMON_SUFFIX = "1.szs";
#endif

        private const string CATEGORY_MAP = "Map";
        private const string CATEGORY_DESIGN = "Design";
        private const string CATEGORY_SOUND = "Sound";

        public const string MAP_PREFIX = "Map_";
        public const string DESIGN_PREFIX = "Design_";
        public const string SOUND_PREFIX = "Sound_";
        private const string BYML_SUFFIX = ".byml";
        #endregion



        #region Document State
        private DateTime lastSaveTime;
        bool isSaved = true;

        public IRevertable LastSavedUndo { get; private set; }

        public readonly Stack<IRevertable> undoStack = new Stack<IRevertable>();
        public readonly Stack<RedoEntry> redoStack = new Stack<RedoEntry>();

        public HashSet<string> enabledLayers = new HashSet<string>();

#if ODYSSEY
        private HashSet<string>[] enabledLayersPerScenario = new HashSet<string>[15];

        public int CurrentScenario { get; private set; } = 0;

        public void SetScenario(int scenario)
        {
            CurrentScenario = scenario;

            enabledLayers = enabledLayersPerScenario[scenario];
        }
#endif

        public virtual bool IsSaved
        {
            get => isSaved;
            set
            {
                if (isSaved != value)
                {
                    isSaved = value;

                    if (value)
                    {
                        if (undoStack.Count == 0)
                            LastSavedUndo = null;
                        else
                            LastSavedUndo = undoStack.Peek();
                    }
                }
            }
        }
        #endregion



        #region ZoneRenderBatch
        public readonly ZoneRenderBatch ZoneBatch = new ZoneRenderBatch();

        public void UpdateRenderBatch()
        {
            ZoneBatch.Clear();

            SceneObjectIterState.InLinks = false;
            foreach (var (listName, objList) in ObjLists)
            {
                if (listName == MAP_PREFIX + "SkyList")
                    continue;

                foreach (I3dWorldObject obj in objList)
                {
                    if (enabledLayers.Contains(obj.Layer))
                        obj.AddToZoneBatch(ZoneBatch);
                }
            }
            SceneObjectIterState.InLinks = true;
            foreach (I3dWorldObject obj in LinkedObjects)
                obj.AddToZoneBatch(ZoneBatch);
        }
        #endregion



        #region public getters
        /// <summary>
        /// Name of this Zone/Stage/Island
        /// </summary>
        public string StageName => StageInfo.StageName;
        /// <summary>
        /// The Directory this Zone/Stage/Island is stored in
        /// </summary>
        public string Directory => StageInfo.Directory;
        #endregion



        #region Stage info
        public StageInfo StageInfo { get; private set; }
        public ByteOrder ByteOrder { get; private set; }

        /// <summary>
        /// Any extra files that may be inside the map
        /// </summary>
        public Dictionary<string, dynamic>[] ExtraFiles { get; private set; } = new Dictionary<string, dynamic>[]
        {
            new Dictionary<string, dynamic>(),
            new Dictionary<string, dynamic>(),
            new Dictionary<string, dynamic>()
        };

        private StageArchiveInfo[] stageArchiveInfos;
        #endregion



        #region Objects
        public Dictionary<string, ObjectList> ObjLists = new Dictionary<string, ObjectList>();

        public ObjectList LinkedObjects = new ObjectList();

        public readonly List<ZonePlacement> ZonePlacements = new List<ZonePlacement>();

        public List<string> availibleLayers = new List<string>();

        private ulong highestObjID = 0;

        public void SubmitID(string id)
        {
            if (id.StartsWith("obj") && ulong.TryParse(id.Substring(3), out ulong objID))
            {
                if (objID > highestObjID)
                    highestObjID = objID;
            }
        }

        private ulong highestRailID = 0;

        public void SubmitRailID(string id)
        {
            if (id.StartsWith("rail") && ulong.TryParse(id.Substring(4), out ulong objID))
            {
                if (objID > highestRailID)
                    highestRailID = objID;
            }
        }

        public string NextObjID() => "obj" + (++highestObjID);

        public string NextRailID() => "rail" + (++highestRailID);
        #endregion



        #region check outside changes and resolve
        public void CheckZoneNameChanges()
        {
            foreach (var placement in ZonePlacements)
            {
                if (placement.ZoneLookupName != placement.Zone.StageName)
                {
                    if (MessageBox.Show($"{placement.Zone.StageName} has a different Name than the Zone referenced in this Stage ({placement.ZoneLookupName}), do you want to reload the original Zone?", "Name change detected", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                    RETRY:
                        if (!TryOpen(Directory, placement.ZoneLookupName, out SM3DWorldZone zone))
                            TryOpen(Program.BaseStageDataPath, placement.ZoneLookupName, out zone);

                        if (zone == null)
                        {
                            if (MessageBox.Show($"{placement.ZoneLookupName} could not be loaded", "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry)
                                goto RETRY;
                            continue;
                        }

                        placement.Zone = zone;
                    }
                    else
                    {
                        placement.ZoneLookupName = placement.Zone.StageName;
                    }
                }
            }
        }

        public void CheckLocalFiles()
        {
            foreach (var stageArcInfo in stageArchiveInfos)
            {
                string fileName = Path.Combine(Directory, stageArcInfo.FileName);

                string dialogText = Program.CurrentLanguage.GetTranslation("ModifiedOutsideText") ?? "{0} was modified outside of Spotlight. Should all extra files be reloaded?";

                if (File.GetLastWriteTime(fileName) > lastSaveTime && MessageBox.Show(
                    string.Format(dialogText, fileName),
                    Program.CurrentLanguage.GetTranslation("ModifiedOutsideHeader") ?? "File Modified",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    if (stageArcInfo.ExtraFileIndex == -1)
                    {
                        ExtraFiles[0].Clear();
                        ExtraFiles[1].Clear();
                        ExtraFiles[2].Clear();
                    }
                    else
                        ExtraFiles[stageArcInfo.ExtraFileIndex].Clear();

                    var sarcFiles = SARCExt.SARC.UnpackRamN(YAZ0.Decompress(fileName)).Files;

                    foreach (var item in stageArcInfo.BymlInfos)
                        sarcFiles.Remove(item.FileName);

                    foreach (var fileEntry in sarcFiles)
                    {
                        LoadExtraFile(fileEntry, stageArcInfo.ExtraFileIndex);
                    }
                }
            }

            lastSaveTime = DateTime.Now;
        }
        #endregion



        #region saving/loading
        #region common
        class StageArchiveInfo
        {
            public StageArchiveInfo(string fileName, int extraFileIndex, StageBymlInfo[] bymlInfos)
            {
                FileName = fileName;
                ExtraFileIndex = extraFileIndex;
                BymlInfos = bymlInfos;
            }

            public string FileName { get; private set; }
            public int ExtraFileIndex { get; private set; }
            public StageBymlInfo[] BymlInfos { get; private set; }
        }

        class StageBymlInfo
        {
            public StageBymlInfo(string fileName, string categoryPrefix, bool containsZones = false)
            {
                FileName = fileName;
                CategoryPrefix = categoryPrefix;
                ContainsZones = containsZones;
            }

            public string FileName { get; private set; }
            public string CategoryPrefix { get; private set; }
            public bool ContainsZones { get; private set; }
        }

        public static bool TryGetStageInfo(string fileName, out StageInfo? stageInfo)
        {
            string levelFileName = Path.GetFileName(fileName);

            string levelName;
            StageArcType arcType;

            if (levelFileName.EndsWith(MAP_SUFFIX))
            {
                levelName = levelFileName.Remove(levelFileName.Length - MAP_SUFFIX.Length);
                arcType = StageArcType.Split;
            }
            else if (levelFileName.EndsWith(DESIGN_SUFFIX))
            {
                levelName = levelFileName.Remove(levelFileName.Length - DESIGN_SUFFIX.Length);
                arcType = StageArcType.Split;
            }
            else if (levelFileName.EndsWith(SOUND_SUFFIX))
            {
                levelName = levelFileName.Remove(levelFileName.Length - SOUND_SUFFIX.Length);
                arcType = StageArcType.Split;
            }
            else if (levelFileName.EndsWith(COMBINED_SUFFIX))
            {
                levelName = levelFileName.Remove(levelFileName.Length - COMBINED_SUFFIX.Length);
                arcType = StageArcType.Combined;
            }
            else
            {
                stageInfo = null;
                return false;
            }

            stageInfo = new StageInfo(Path.GetDirectoryName(fileName), levelName, arcType);

            return true;
        }


        public static Dictionary<StageInfo, SM3DWorldZone> loadedZones = new Dictionary<StageInfo, SM3DWorldZone>();

        public void Unload()
        {
            loadedZones.Remove(StageInfo);
        }
        #endregion

        #region loading
        //these methods just cover getting the bymls that contain the object placements and all the extra files

        //for actually parsing the byml and getting it's objects it uses the LevelReader class
        //afterwards EvaluateLayers uses all read objects to evaluate which layers are used

        public static bool TryOpen(string directory, string stageName, out SM3DWorldZone zone)
        {
            if (TryOpen(new StageInfo(directory, stageName, StageArcType.Split), out zone))
                return true;
            else if (TryOpen(new StageInfo(directory, stageName, StageArcType.Combined), out zone))
                return true;
            else
                return false;
        }

        public static bool TryOpen(string fileName, out SM3DWorldZone zone)
        {
            if (TryGetStageInfo(fileName, out var loadingInfo))
            {
                return TryOpen(loadingInfo.Value, out zone);
            }
            else
            {
                zone = null;
                return false;
            }
        }

        public static bool TryOpen(StageInfo stageInfo, out SM3DWorldZone zone)
        {
            if (loadedZones.TryGetValue(stageInfo, out zone))
                return true;


            bool hasStageByml = false;
            ByteOrder byteOrder = 0;

            Dictionary<string, SarcData> loadedArchives = new Dictionary<string, SarcData>();
            List<StageArchiveInfo> loadInfos = new List<StageArchiveInfo>();

            #region local helper functions
            StageBymlInfo[] GenerateFileInfos(SarcData sarc, params string[] categoryNames)
            {
                List<StageBymlInfo> infos = new List<StageBymlInfo>();

                foreach (var categoryName in categoryNames)
                {
                    string fileName = stageInfo.StageName + categoryName + ".byml";
                    if (!sarc.Files.ContainsKey(fileName))
                        continue;

                    infos.Add(new StageBymlInfo(stageInfo.StageName + categoryName + ".byml", categoryName + "_",
                        categoryName == CATEGORY_MAP));

                    hasStageByml = true; //A little bit hacky I know
                }

                return infos.ToArray();
            }

            void AddArchive(string categoryName, int extraFilesIndex)
            {
                string arcName = stageInfo.StageName + categoryName + COMMON_SUFFIX;
                string fileName = Path.Combine(stageInfo.Directory, arcName);

                if (!File.Exists(fileName))
                    return;

                SarcData sarc = SARC.UnpackRamN(OldYAZ0.Decompress(File.ReadAllBytes(fileName)));
                loadedArchives.Add(arcName, sarc);
                byteOrder = sarc.byteOrder;

                loadInfos.Add(new StageArchiveInfo(arcName, extraFilesIndex, GenerateFileInfos(sarc, categoryName)));
            }
            #endregion


            switch (stageInfo.StageArcType)
            {
                case StageArcType.Split:
                    AddArchive(CATEGORY_MAP, 0);
                    AddArchive(CATEGORY_DESIGN, 1);
                    AddArchive(CATEGORY_SOUND, 2);
                    break;

                case StageArcType.Combined:
                    string arcName = stageInfo.StageName + COMBINED_SUFFIX;
                    string fileName = Path.Combine(stageInfo.Directory, arcName);

                    if (!File.Exists(fileName))
                        break;

                    SarcData sarc = SARC.UnpackRamN(OldYAZ0.Decompress(File.ReadAllBytes(fileName)));
                    loadedArchives.Add(arcName, sarc);
                    byteOrder = sarc.byteOrder;

                    loadInfos.Add(new StageArchiveInfo(arcName, -1, GenerateFileInfos(sarc,
                        CATEGORY_MAP, CATEGORY_DESIGN, CATEGORY_SOUND)));
                    break;
            }

            zone = new SM3DWorldZone(loadInfos.ToArray(), stageInfo, byteOrder, loadedArchives);

            if (hasStageByml)
                loadedZones.Add(stageInfo, zone);

            return hasStageByml;
        }

        private SM3DWorldZone(StageArchiveInfo[] stageArchiveInfos, StageInfo stageInfo, ByteOrder byteOrder, Dictionary<string, SarcData> loadedArchives)
        {
            this.stageArchiveInfos = stageArchiveInfos;

            StageInfo = stageInfo;
            ByteOrder = byteOrder;

            LevelReader levelReader = new LevelReader(this);

            foreach (var stageArchiveInfo in stageArchiveInfos)
            {
                var sarc = loadedArchives[stageArchiveInfo.FileName];

                foreach (var bymlInfo in stageArchiveInfo.BymlInfos)
                {
                    Dictionary<long, I3dWorldObject> objectsByReference = new Dictionary<long, I3dWorldObject>();

                    levelReader.LoadStageByml(new ByamlIterator(new MemoryStream(sarc.Files[bymlInfo.FileName])), bymlInfo.CategoryPrefix);

                    sarc.Files.Remove(bymlInfo.FileName);
                }

                foreach (var fileEntry in sarc.Files)
                {
                    LoadExtraFile(fileEntry, stageArchiveInfo.ExtraFileIndex);
                }
            }

            EvaluateLayers(levelReader);


            lastSaveTime = DateTime.Now;
        }


        private void LoadExtraFile(KeyValuePair<string, byte[]> fileEntry, int extraFilesIndex = -1)
        {
            if (extraFilesIndex == -1)
            {
                switch (fileEntry.Key)
                {
                    case "CameraParam.byml":
                    case "InterpoleParam.byml":
                    case "Island.byml":
                    case "InitClipping.byml":
                        extraFilesIndex = 0; //Map
                        break;
                    default:
                        extraFilesIndex = 1; //Design
                        break;
                }
            }

            if ((fileEntry.Value[0] << 8 | fileEntry.Value[1]) == ByamlFile.BYAML_MAGIC || //BigEndian
                            (fileEntry.Value[1] << 8 | fileEntry.Value[0]) == ByamlFile.BYAML_MAGIC)   //LittleEndian
                ExtraFiles[extraFilesIndex].Add(fileEntry.Key, ByamlFile.FastLoadN(new MemoryStream(fileEntry.Value)));
            else
                ExtraFiles[extraFilesIndex].Add(fileEntry.Key, fileEntry.Value);
        }


        private void EvaluateLayers(LevelReader levelReader)
        {
#if ODYSSEY
            for (int i = 0; i < enabledLayersPerScenario.Length; i++)
            {
                enabledLayersPerScenario[i] = new HashSet<string>();
            }


            var scenarioBitFieldsPerLayerWithCounts = new Dictionary<string, //layer
                                                 Dictionary<ushort, int>>(); //scenarioBitField, count

            var scenarioBitFieldsPerLayerWithCounts_linked = new Dictionary<string, //layer
                                                        Dictionary<ushort, int>>(); //scenarioBitField, count

            var scenarioBitFields = new HashSet<ushort>();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void CountScenarioBits(in string layer, in ushort scenarioBits)
            {
                scenarioBitFieldsPerLayerWithCounts.Require(layer).AddOne(scenarioBits);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void CountScenarioBitsInLinked(in string layer, in ushort scenarioBits)
            {
                scenarioBitFieldsPerLayerWithCounts_linked.Require(layer).AddOne(scenarioBits);
            }



            foreach (var (obj, scenarioBits, isLinked) in levelReader.GetObjectsWithScenarioBits())
            {
                if (isLinked)
                {
                    CountScenarioBitsInLinked(obj.Layer, scenarioBits);
                }
                else
                    CountScenarioBits(obj.Layer, scenarioBits);

                scenarioBitFields.Add(scenarioBits);
            }

            foreach (var (placement, scenarioBits) in levelReader.GetZonePlacementsWithScenarioBits())
            {
                CountScenarioBits(placement.Layer, scenarioBits);

                scenarioBitFields.Add(scenarioBits);
            }

            var layersPerBitFieldWithCounts = new Dictionary<ushort, List<(string layer, int count)>>();

            foreach (var (layer, bitFieldsWithCounts) in scenarioBitFieldsPerLayerWithCounts_linked
                .Where(x => !scenarioBitFieldsPerLayerWithCounts.ContainsKey(x.Key))
                .Concat(scenarioBitFieldsPerLayerWithCounts))
            {
                if (bitFieldsWithCounts.Count == 0)
                    continue;

                availibleLayers.Add(layer);

                ushort maxBitfield = 0;
                int maxCount = 0;

                foreach (var (bitField, count) in bitFieldsWithCounts)
                {
                    if (count > maxCount)
                    {
                        maxBitfield = bitField;
                        maxCount = count;
                    }
                }

                layersPerBitFieldWithCounts.Require(maxBitfield).Add((layer, maxCount));
            }

            #region Calculate including layers per bitfield
            //should perform well enough for all levels since there are
            //not too many different scenarioBits
            var includingLayersPerBitField = new Dictionary<ushort, HashSet<string>>();

            foreach (var bitField in scenarioBitFields)
            {
                includingLayersPerBitField.Add(bitField, new HashSet<string>(bitField));
            }

            foreach (var (bitField, layersWithCounts) in layersPerBitFieldWithCounts)
            {
                foreach (var (keyBitField, includingLayers) in includingLayersPerBitField)
                {
                    //figure out if the bitfield includes the keyBitField

                    if ((bitField & keyBitField) == keyBitField)
                        includingLayers.UnionWith(layersWithCounts.Select(x => x.layer));
                }
            }

            #endregion

            var layersPerBitField_lookUp = new Dictionary<ushort, (string preferred, HashSet<string> accepted)>();

            foreach (var (bitField, unsortedLayers) in layersPerBitFieldWithCounts)
            {
                string maxLayer = null;

                HashSet<string> layerSet = new HashSet<string>();

                int max = 0;

                foreach (var (layer, count) in unsortedLayers)
                {
                    layerSet.Add(layer);

                    if (count > max)
                    {
                        max = count;
                        maxLayer = layer;
                    }
                }

                layersPerBitField_lookUp.Add(bitField, (maxLayer, layerSet));
            }

            var emptySet = new HashSet<string>();



            #region resolve conflicts


            string HandleLayer(string layer, ushort scenarioBits)
            {
                (string preferred, HashSet<string> accepted) entry;

                if (!layersPerBitField_lookUp.TryGetValue(scenarioBits, out entry))
                {
                    string newLayer = GenerateLayerName(scenarioBits);

                    entry = (newLayer, emptySet);

                    layersPerBitField_lookUp.Add(scenarioBits, entry);

                    availibleLayers.Add(newLayer);


                    foreach (var scenario in BitUtils.AllSetBits(scenarioBits))
                    {
                        enabledLayersPerScenario[scenario].Add(newLayer);
                    }
                }

                if (!entry.accepted.Contains(layer))
                {
                    return entry.preferred;
                }

                return layer;
            }

            foreach (var (obj, scenarioBits, isLinked) in levelReader.GetObjectsWithScenarioBits())
            {
                if (isLinked && includingLayersPerBitField.TryGetValue(scenarioBits, out var layers) && layers.Contains(obj.Layer))
                    //the objects exists in other scenarios it's just not linked there, that's why the scenarioBits are incorrect
                    continue; //so we can ignore it

                obj.Layer = HandleLayer(obj.Layer, scenarioBits);
            }

            foreach (var (placement, scenarioBits) in levelReader.GetZonePlacementsWithScenarioBits())
            {
                placement.Layer = HandleLayer(placement.Layer, scenarioBits);
            }
            #endregion
#else
            availibleLayers = levelReader.readLayers.ToList();
#endif

            List<string> commons = new List<string>();
            List<string> scenarios = new List<string>();
            List<string> others = new List<string>();

            foreach (var layer in availibleLayers)
            {
                if (layer.StartsWith("Common"))
                    commons.Add(layer);
                else if (layer.StartsWith("Scenario"))
                    scenarios.Add(layer);
                else
                    others.Add(layer);
            }

            commons.Sort();
            scenarios.Sort();
            others.Sort();

            availibleLayers = commons;
            availibleLayers.AddRange(scenarios);
            availibleLayers.AddRange(others);

#if ODYSSEY
            #region calculate enabled layers per scenario

            foreach (var (bitField, (_, layers)) in layersPerBitField_lookUp)
            {
                foreach (var index in BitUtils.AllSetBits(bitField))
                {
                    enabledLayersPerScenario[index].UnionWith(layers);
                }
            }
            #endregion

            enabledLayers = enabledLayersPerScenario[0];
#else
            enabledLayers = availibleLayers.ToHashSet();
#endif
        }

        private static string GenerateLayerName(ushort scenarioBits)
        {
            StringBuilder sb = new StringBuilder("S");

            {
                var bits = scenarioBits;

                bool prevBitWasSet = false;

                int streekStart = -1;

                for (int scenario = 0; scenario < 16; scenario++)
                {
                    if ((bits & 0x1) == 1) //scenario bit set at index
                    {
                        if (!prevBitWasSet)
                            streekStart = scenario;

                        prevBitWasSet = true;
                    }
                    else
                    {
                        if (prevBitWasSet)
                        {
                            var a = streekStart;
                            var b = scenario - 1;

                            if (a == b)
                                sb.Append(a + 1);
                            else if (a + 1 == b)
                            {
                                sb.Append(a + 1);
                                sb.Append('_');
                                sb.Append(b + 1);
                            }
                            else
                            {
                                sb.Append(a + 1);
                                sb.Append('-');
                                sb.Append(b + 1);
                            }

                            sb.Append('_');
                        }

                        prevBitWasSet = false;
                    }

                    bits >>= 1;
                }
            }

            string newLayer = sb.ToString().Trim('_');
            return newLayer;
        }
        #endregion

        #region saving
        /// <summary>
        /// Saves this <see cref="SM3DWorldZone"/> to the FileSystem
        /// </summary>
        /// <param name="fileName">the file name of the Main Archive, will be used to determine how to save the zone, where to save it and what to save</param>
        /// <returns>true if the save succeeded, false if it failed</returns>
        public bool Save(string fileName, ByteOrder? newEndian = null)
        {
            if (TryGetStageInfo(fileName, out StageInfo? newStageInfo))
                return Save(newStageInfo, newEndian);
            else
                return false;
        }

        /// <summary>
        /// Saves this <see cref="SM3DWorldZone"/> to the FileSystem
        /// </summary>
        /// <returns>true if the save succeeded, false if it failed</returns>
        public bool Save(StageInfo? newStageInfo = null, ByteOrder? newEndian = null)
        {
            //Change what needs to be changed
            if (newStageInfo.HasValue)
            {
                loadedZones.Remove(StageInfo);
                StageInfo = newStageInfo.Value;
                loadedZones.Add(StageInfo, this);
            }

            if (newEndian.HasValue)
                ByteOrder = newEndian.Value;


            //rebuild file structure and make sure that everything that should be saved will be saved
            stageArchiveInfos = GetSaveArchiveInfos(GetSaveableCategories(), StageInfo);

            //actually save everything and mark this zone as saved
            SaveInternal();
            IsSaved = true;

            lastSaveTime = DateTime.Now;

            return true;
        }

        public string[] GetSaveFileNames(StageInfo stageInfo)
        {
            return GetSaveArchiveInfos(GetSaveableCategories(), stageInfo).Select(x => x.FileName).ToArray();
        }

        private static StageArchiveInfo[] GetSaveArchiveInfos((string name, int extraFileIndex)[] categoriesToSave, StageInfo stageInfo)
        {
            StageBymlInfo BymlInfo(string categoryName) => new StageBymlInfo(stageInfo.StageName + categoryName + BYML_SUFFIX, categoryName + "_", categoryName == CATEGORY_MAP);

            List<StageArchiveInfo> infos = new List<StageArchiveInfo>();

            switch (stageInfo.StageArcType)
            {
                case StageArcType.Combined:
                    List<StageBymlInfo> bymlInfos = new List<StageBymlInfo>();

                    foreach ((string name, int extraFileIndex) in categoriesToSave)
                    {
                        bymlInfos.Add(BymlInfo(name));
                    }

                    infos.Add(new StageArchiveInfo(stageInfo.StageName + COMBINED_SUFFIX, -1, bymlInfos.ToArray()));
                    break;

                case StageArcType.Split:
                    foreach ((string name, int extraFileIndex) in categoriesToSave)
                    {
                        infos.Add(new StageArchiveInfo(stageInfo.StageName + name + COMMON_SUFFIX, extraFileIndex,
                            new StageBymlInfo[] { BymlInfo(name) }));
                    }

                    break;
            }

            return infos.ToArray();
        }

        public (string name, int extraFileIndex)[] GetSaveableCategories()
        {
            var usedPrefixes = stageArchiveInfos.SelectMany(x => x.BymlInfos.Select(y => y.CategoryPrefix));

            bool CheckCategory(string categoryPrefix, int extraFileIndex)
            {
                if (usedPrefixes.Contains(categoryPrefix))
                    return true;

                if (ExtraFiles[extraFileIndex].Count > 0)
                    return true;

                foreach (var (name, list) in ObjLists)
                {
                    if (name.StartsWith(categoryPrefix) && list.Count > 0)
                        return true;
                }

                return false; //nothing saveable found
            }


            List<(string name, int extraFileIndex)> saveableCategories = new List<(string name, int extraFileIndex)>();

            //the Map Category should always be saved
            saveableCategories.Add((CATEGORY_MAP, 0));


            if (CheckCategory(CATEGORY_DESIGN, 1))
                saveableCategories.Add((CATEGORY_DESIGN, 1));

            if (CheckCategory(CATEGORY_SOUND, 2))
                saveableCategories.Add((CATEGORY_SOUND, 2));

            return saveableCategories.ToArray();
        }

        private void SaveInternal()
        {
            void SaveExtraFiles(SARCExt.SarcData sarcData, int extraFileIndex)
            {
                foreach (KeyValuePair<string, dynamic> fileEntry in ExtraFiles[extraFileIndex])
                    SaveExtraFile(sarcData, fileEntry);
            }

            foreach (var stageArcInfo in stageArchiveInfos)
            {
                SARCExt.SarcData sarcData = new SARCExt.SarcData()
                {
                    HashOnly = false,
                    endianness = ByteOrder,
                    Files = new Dictionary<string, byte[]>()
                };



                if (stageArcInfo.ExtraFileIndex == -1)
                {
                    SaveExtraFiles(sarcData, 0);
                    SaveExtraFiles(sarcData, 1);
                    SaveExtraFiles(sarcData, 2);
                }
                else
                    SaveExtraFiles(sarcData, stageArcInfo.ExtraFileIndex);

                foreach (var bymlInfo in stageArcInfo.BymlInfos)
                {
                    using (MemoryStream stream = new MemoryStream())
                    {
                        LegacyWriteStageByml(stream, bymlInfo.CategoryPrefix, bymlInfo.ContainsZones);

                        sarcData.Files.Add(bymlInfo.FileName, stream.ToArray());
                    }
                }

                byte[] b = SARCExt.SARC.PackN(sarcData).Item2;

                File.WriteAllBytes(Path.Combine(Directory, "RAW_" + stageArcInfo.FileName), b);
                File.WriteAllBytes(Path.Combine(Directory, stageArcInfo.FileName), OldYAZ0.Compress(b));
            }
        }

        private static void SaveExtraFile(SARCExt.SarcData sarcData, KeyValuePair<string, dynamic> fileEntry)
        {
            if (fileEntry.Value is BymlFileData)
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    ByamlFile.FastSaveN(stream, fileEntry.Value);
                    sarcData.Files.Add(fileEntry.Key, stream.ToArray());
                }
            }
            else if (fileEntry.Value is byte[])
            {
                sarcData.Files.Add(fileEntry.Key, fileEntry.Value);
            }
            else
                throw new Exception("The extra file " + fileEntry.Key + "has no way to save");
        }

        // Odysey not supported.
        private void LegacyWriteStageByml(MemoryStream stream, string prefix, bool saveZonePlacements)
        {
            ByamlNodeWriter writer = new ByamlNodeWriter(stream, false, ByteOrder, 1);

            #region Create ZoneList
            ByamlNodeWriter.ArrayNode zonesNode = writer.CreateArrayNode();

            if (saveZonePlacements)
            {
                int zoneID = 0;

                foreach (var zonePlacement in ZonePlacements)
                {
                    ByamlNodeWriter.DictionaryNode objNode = writer.CreateDictionaryNode();

                    objNode.AddDynamicValue("Comment", null);
                    objNode.AddDynamicValue("Id", "zone" + zoneID++);
                    objNode.AddDynamicValue("IsLinkDest", false);
                    objNode.AddDynamicValue("LayerConfigName", "Common");

                    {
                        objNode.AddDynamicValue("Links", new Dictionary<string, dynamic>(), true);
                    }

                    objNode.AddDynamicValue("ModelName", null);
                    objNode.AddDynamicValue("Rotate", LevelIO.Vector3ToDict(zonePlacement.Rotation), true);
                    objNode.AddDynamicValue("Scale", LevelIO.Vector3ToDict(Vector3.One), true);
                    objNode.AddDynamicValue("Translate", LevelIO.Vector3ToDict(zonePlacement.Position, 100f), true);

                    objNode.AddDynamicValue("UnitConfig", new Dictionary<string, dynamic>
                    {
                        ["DisplayName"] = "ï¿½Rï¿½Cï¿½ï¿½(ï¿½ï¿½ï¿½ï¿½ï¿½Oï¿½zï¿½u)",
                        ["DisplayRotate"] = LevelIO.Vector3ToDict(Vector3.Zero),
                        ["DisplayScale"] = LevelIO.Vector3ToDict(Vector3.One),
                        ["DisplayTranslate"] = LevelIO.Vector3ToDict(Vector3.Zero),
                        ["GenerateCategory"] = "",
                        ["ParameterConfigName"] = "Zone",
                        ["PlacementTargetFile"] = CATEGORY_MAP
                    }, true);

                    objNode.AddDynamicValue("UnitConfigName", zonePlacement.Zone.StageName);

                    zonesNode.AddDictionaryNodeRef(objNode, true);
                }
            }
            #endregion

            HashSet<I3dWorldObject> alreadyWrittenObjs = new HashSet<I3dWorldObject>();

            ByamlNodeWriter.DictionaryNode rootNode = writer.CreateDictionaryNode();

            rootNode.AddDynamicValue("FilePath", "N/A");

            ByamlNodeWriter.ArrayNode objsNode = writer.CreateArrayNode();

            foreach (var (listName, objList) in ObjLists)
            {
                if (!listName.StartsWith(prefix)) //ObjList is not part of the Category
                    continue;

                ByamlNodeWriter.ArrayNode objListNode = writer.CreateArrayNode();

                foreach (I3dWorldObject obj in objList)
                {
                    if (!alreadyWrittenObjs.Contains(obj))
                    {
                        ByamlNodeWriter.DictionaryNode objNode = writer.CreateDictionaryNode(obj);
                        obj.LegacySave(alreadyWrittenObjs, writer, objNode, false);
                        objListNode.AddDictionaryNodeRef(objNode);
                        objsNode.AddDictionaryNodeRef(objNode);
                    }
                    else
                    {
                        objListNode.AddDictionaryRef(obj);
                        objsNode.AddDictionaryRef(obj);
                    }
                }
                rootNode.AddArrayNodeRef(listName.Substring(prefix.Length), objListNode, true);
            }
            rootNode.AddArrayNodeRef("Objs", objsNode, true);
            if (saveZonePlacements)
                rootNode.AddArrayNodeRef("ZoneList", zonesNode, true);

            writer.Write(rootNode, true);
        }

        private void WriteStageByml(MemoryStream stream, string prefix, bool saveZonePlacements)
        {
            ByamlNodeWriter writer = new ByamlNodeWriter(stream, false, ByteOrder, 1);

#if ODYSSEY
            ByamlNodeWriter.ArrayNode rootNode = writer.CreateArrayNode();

            for (int scenario = 0; scenario < 15; scenario++)
            {
                ByamlNodeWriter.DictionaryNode scenarioNode = writer.CreateDictionaryNode();

                SaveObjectLists(scenarioNode, writer, prefix, saveZonePlacements, enabledLayersPerScenario[scenario]);

                rootNode.AddDictionaryNodeRef(scenarioNode, true);
            }

            writer.Write(rootNode, true);
#else
            ByamlNodeWriter.DictionaryNode rootNode = writer.CreateDictionaryNode();

            SaveObjectLists(rootNode, writer, prefix, saveZonePlacements, availibleLayers.ToHashSet());


            rootNode.AddDynamicValue("FilePath", "N/A");

            writer.Write(rootNode, true);
#endif
        }


        private void SaveObjectLists(ByamlNodeWriter.DictionaryNode listsNode, ByamlNodeWriter writer, string prefix, bool saveZonePlacements, HashSet<string> layers)
        {
            //apologies for the bad code, merging odyssey and 3d world saving code isn't an easy task

            #region Create ZoneList
            ByamlNodeWriter.ArrayNode zonesNode = writer.CreateArrayNode();

            if (saveZonePlacements)
            {
                int zoneID = 0;

                foreach (var zonePlacement in ZonePlacements)
                {
                    if (!layers.Contains(zonePlacement.Layer))
                    {
                        zoneID++;
                        continue;
                    }

                    ByamlNodeWriter.DictionaryNode objNode = writer.CreateDictionaryNode();

                    zonePlacement.Save(writer, objNode, zoneID++);

                    zonesNode.AddDictionaryNodeRef(objNode, true);
                }
            }
            #endregion

            HashSet<I3dWorldObject> alreadyWrittenObjs = new HashSet<I3dWorldObject>();

#if !ODYSSEY
            ByamlNodeWriter.ArrayNode objsNode = writer.CreateArrayNode();
#endif
            foreach (var (listName, objList) in ObjLists)
            {
#if ODYSSEY
                if (objList.Count == 0)
                    continue; //level files in Odyssey don't contain empty lists
#endif

                if (!listName.StartsWith(prefix)) //ObjList is not part of the Category
                    continue;

                ByamlNodeWriter.ArrayNode objListNode = writer.CreateArrayNode();

                void WriteObjNode(ByamlNodeWriter.DictionaryNode node)
                {
                    objListNode.AddDictionaryNodeRef(node);
#if !ODYSSEY
                    objsNode.AddDictionaryNodeRef(node);
#endif
                }

                void WriteObjRef(I3dWorldObject obj)
                {
                    objListNode.AddDictionaryRef(obj);
#if !ODYSSEY
                    objsNode.AddDictionaryRef(obj);
#endif
                }

                foreach (I3dWorldObject obj in objList)
                {
                    if (!layers.Contains(obj.Layer))
                        continue;

                    if (!alreadyWrittenObjs.Contains(obj))
                    {
                        ByamlNodeWriter.DictionaryNode objNode = writer.CreateDictionaryNode(obj);
                        obj.Save(alreadyWrittenObjs, writer, objNode, layers, false);
                        WriteObjNode(objNode);
                    }
                    else
                    {
                        WriteObjRef(obj);
                    }
                }
#if ODYSSEY
                listsNode.AddArrayNodeRef(listName.Substring(prefix.Length), objListNode, true);
#endif
            }


            if (saveZonePlacements)
                listsNode.AddArrayNodeRef("ZoneList", zonesNode, true);

#if !ODYSSEY
            listsNode.AddArrayNodeRef("Objs", objsNode, true);
#endif
        }
        #endregion
        #endregion
    }
}








public static class Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TValue Require<TKey, TValue>(this Dictionary<TKey, TValue> self, TKey key) where TValue : new()
    {
        if (self.TryGetValue(key, out TValue value))
            return value;
        else
        {
            TValue newVal = new TValue();

            self[key] = newVal;

            return newVal;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddOne<TKey>(this Dictionary<TKey, int> self, TKey key)
    {
        if (self.TryGetValue(key, out int value))
            self[key] = value++;
        else
            self[key] = 1;
    }
}

public static class BitUtils
{
    public static IEnumerable<int> AllSetBits(ushort bits)
    {
        int index = 0;

        while (bits > 0)
        {
            if ((bits & 0x1) == 1) //scenario bit set at index
                yield return index;

            bits >>= 1;
            index++;
        }
    }
}