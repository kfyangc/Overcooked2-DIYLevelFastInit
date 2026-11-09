using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DIYLevelFastInit
{
    /// <summary>Extracts scalar catalog data from type trees without creating Unity resource graphs.</summary>
    internal sealed class SerializedCatalogReader
    {
        private enum Kind { Text, Pointer, Pointers, Texts, Integer, Float }
        private sealed class Field
        {
            internal string Name, Type;
            internal int Depth, Size, Flags, Meta, End;
        }
        private sealed class ObjectType
        {
            internal int ClassId;
            internal Field[] Fields;
            internal bool Has(string name)
            {
                for (int i = 1; i < Fields.Length; i = Fields[i].End)
                    if (Fields[i].Name == name) return true;
                return false;
            }
        }
        private sealed class ObjectEntry
        {
            internal ObjectType Type;
            internal long Offset;
            internal int Size;
        }
        private struct Pointer
        {
            internal int File;
            internal long Id;
        }

        private readonly InfoBundleGraph.CatalogSource _source;
        private readonly InfoBundleGraph.CatalogSource.FileEntry _file;
        private readonly Dictionary<long, ObjectEntry> _objects = new Dictionary<long, ObjectEntry>();
        private readonly Dictionary<long, string> _scripts = new Dictionary<long, string>();
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private const int MaxMetadata = 8 * 1024 * 1024, MaxObject = 1024 * 1024, MaxItems = 20000;

        private static readonly Dictionary<string, Kind> SetFields = new Dictionary<string, Kind>
        {
            { "m_Name", Kind.Text }, { "levelSetName", Kind.Text }, { "levelSetNameZH", Kind.Text },
            { "author", Kind.Text }, { "uid", Kind.Text }, { "version", Kind.Text }, { "levelInfos", Kind.Pointers },
            { "m_Script", Kind.Pointer }
        };
        private static readonly Dictionary<string, Kind> LevelFields = new Dictionary<string, Kind>
        {
            { "m_Name", Kind.Text }, { "levelName", Kind.Text }, { "levelNameZH", Kind.Text }, { "sceneName", Kind.Text },
            { "dependencies", Kind.Texts }, { "config_1p", Kind.Pointer }, { "config_2p", Kind.Pointer },
            { "config_3p", Kind.Pointer }, { "config_4p", Kind.Pointer }, { "m_Script", Kind.Pointer }
        };
        private static readonly Dictionary<string, Kind> PlayerFields = new Dictionary<string, Kind>
        {
            { "orderLifeTime", Kind.Integer }, { "timeBetweenOrders", Kind.Integer }, { "plateReturnTime", Kind.Integer },
            { "roundTime", Kind.Integer }, { "survivalTimeMultiplier", Kind.Float }, { "m_OneStarScore", Kind.Integer },
            { "m_TwoStarScore", Kind.Integer }, { "m_ThreeStarScore", Kind.Integer }, { "m_FourStarScore", Kind.Integer },
            { "m_Script", Kind.Pointer }
        };
        private static readonly Dictionary<string, Kind> ScriptFields = new Dictionary<string, Kind>
        {
            { "m_ClassName", Kind.Text }, { "m_Namespace", Kind.Text }, { "m_AssemblyName", Kind.Text }
        };

        private SerializedCatalogReader(InfoBundleGraph.CatalogSource source, InfoBundleGraph.CatalogSource.FileEntry file)
        {
            _source = source;
            _file = file;
            byte[] header = source.Read(file, 0, 20);
            int metadataSize = Big32(header, 0), size = Big32(header, 4), version = Big32(header, 8), dataOffset = Big32(header, 12);
            if (version != 17 || header[16] != 0 || size != file.Size || metadataSize < 1 || metadataSize > MaxMetadata
                || dataOffset < 20 || dataOffset > MaxMetadata || metadataSize > dataOffset - 20 || dataOffset > file.Size)
                throw new IOException("Unsupported serialized catalog header");
            using (BinaryReader reader = new BinaryReader(new MemoryStream(source.Read(file, 20, metadataSize), false)))
            {
                CString(reader, 128);
                reader.ReadInt32();
                if (reader.ReadByte() != 1) throw new IOException("Catalog type trees are absent");
                ObjectType[] types = new ObjectType[Count(reader, 4096)];
                for (int i = 0; i < types.Length; i++)
                {
                    source.CheckCancelled();
                    int classId = reader.ReadInt32();
                    reader.ReadByte();
                    reader.ReadInt16();
                    if (classId == 114) SkipBytes(reader, 16);
                    SkipBytes(reader, 16);
                    types[i] = new ObjectType { ClassId = classId, Fields = ReadTree(reader) };
                }
                int objects = Count(reader, 200000);
                HashSet<long> ids = new HashSet<long>();
                for (int i = 0; i < objects; i++)
                {
                    Align(reader);
                    long id = reader.ReadInt64();
                    long offset = dataOffset + (long)reader.ReadUInt32();
                    uint length = reader.ReadUInt32();
                    int type = reader.ReadInt32();
                    if (type < 0 || type >= types.Length || offset < dataOffset || offset > file.Size - length || id == 0 || !ids.Add(id))
                        throw new IOException("Invalid catalog object table");
                    if (types[type].ClassId == 114 || types[type].ClassId == 115)
                    {
                        _objects.Add(id, new ObjectEntry { Type = types[type], Offset = offset, Size = (int)length });
                    }
                }
            }
        }

        internal static SetMetadata Read(DirectoryInfo dir, FileInfo file, Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
            SetMetadata result = null;
            using (InfoBundleGraph.CatalogSource source = new InfoBundleGraph.CatalogSource(file.FullName, cancelled))
                foreach (InfoBundleGraph.CatalogSource.FileEntry entry in source.Files)
                {
                    SerializedCatalogReader reader = new SerializedCatalogReader(source, entry);
                    SetMetadata candidate = reader.ReadSet();
                    if (candidate == null) continue;
                    if (result != null) throw new IOException("Ambiguous LevelSetInfo in catalog source");
                    result = candidate;
                }
            FileInfo current = new FileInfo(file.FullName);
            if (current.Length != length || current.LastWriteTimeUtc.Ticks != modified) throw new IOException("Catalog source changed during metadata read");
            if (result == null) throw new IOException("No supported LevelSetInfo in catalog source");
            result.schema = 2;
            result.directory = dir.FullName;
            result.infoFile = file.Name;
            result.length = length;
            result.modified = modified;
            if (!result.IsValid(dir, current)) throw new IOException("Invalid extracted catalog metadata");
            return result;
        }

        private SetMetadata ReadSet()
        {
            SetMetadata result = null;
            foreach (ObjectEntry obj in _objects.Values)
            {
                if (obj.Type.ClassId != 114 || !obj.Type.Has("levelSetName") || !obj.Type.Has("levelInfos")) continue;
                Dictionary<string, object> values = ReadObject(obj, SetFields, "LevelSetInfoSO");
                if ((string)values["m_Name"] != "LevelSetInfo") continue;
                if (result != null) throw new IOException("Ambiguous LevelSetInfo object");
                Pointer[] levels = (Pointer[])values["levelInfos"];
                result = new SetMetadata { name = (string)values["m_Name"], levelSetName = (string)values["levelSetName"],
                    levelSetNameZH = (string)values["levelSetNameZH"], author = (string)values["author"],
                    uid = (string)values["uid"], version = (string)values["version"], levels = new LevelMetadata[levels.Length] };
                for (int i = 0; i < levels.Length; i++)
                {
                    values = ReadObject(Resolve(levels[i]), LevelFields, "LevelInfoSO");
                    LevelMetadata level = new LevelMetadata { name = (string)values["m_Name"], levelName = (string)values["levelName"],
                        levelNameZH = (string)values["levelNameZH"], sceneName = (string)values["sceneName"],
                        dependencies = (string[])values["dependencies"], players = new PlayerMetadata[4] };
                    for (int p = 0; p < 4; p++)
                    {
                        Dictionary<string, object> player = ReadObject(Resolve((Pointer)values["config_" + (p + 1) + "p"]), PlayerFields, "LevelConfigSetupPerPlayerCountSO");
                        level.players[p] = new PlayerMetadata { orderLifeTime = (int)player["orderLifeTime"],
                            timeBetweenOrders = (int)player["timeBetweenOrders"], plateReturnTime = (int)player["plateReturnTime"],
                            roundTime = (int)player["roundTime"], survivalTimeMultiplier = (float)player["survivalTimeMultiplier"],
                            one = (int)player["m_OneStarScore"], two = (int)player["m_TwoStarScore"],
                            three = (int)player["m_ThreeStarScore"], four = (int)player["m_FourStarScore"] };
                    }
                    result.levels[i] = level;
                }
            }
            return result;
        }

        private ObjectEntry Resolve(Pointer pointer)
        {
            ObjectEntry entry;
            if (pointer.File != 0 || pointer.Id == 0 || !_objects.TryGetValue(pointer.Id, out entry))
                throw new IOException("Catalog metadata pointer is external, missing or unsupported");
            return entry;
        }

        private Dictionary<string, object> ReadObject(ObjectEntry obj, Dictionary<string, Kind> selected, string expectedScript = null)
        {
            _source.CheckCancelled();
            if (obj.Size > MaxObject || obj.Offset % 4 != 0) throw new IOException("Unsupported catalog object extent");
            Field[] fields = obj.Type.Fields;
            Dictionary<string, object> values = new Dictionary<string, object>();
            using (BinaryReader reader = new BinaryReader(new MemoryStream(_source.Read(_file, obj.Offset, obj.Size), false)))
            {
                for (int i = 1; i < fields.Length; i = fields[i].End)
                {
                    Kind kind;
                    if (selected.TryGetValue(fields[i].Name, out kind)) values.Add(fields[i].Name, ReadValue(reader, fields, i, kind));
                    else SkipField(reader, fields, i);
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Catalog object layout did not consume its extent");
            }
            if (values.Count != selected.Count) throw new IOException("Catalog object is missing required scalar fields");
            if (expectedScript != null)
            {
                Pointer pointer = (Pointer)values["m_Script"];
                string identity;
                if (pointer.File != 0 || pointer.Id == 0) throw new IOException("Catalog script pointer is external or missing");
                if (!_scripts.TryGetValue(pointer.Id, out identity))
                {
                    ObjectEntry script = Resolve(pointer);
                    if (script.Type.ClassId != 115) throw new IOException("Catalog script pointer does not reference a MonoScript");
                    Dictionary<string, object> definition = ReadObject(script, ScriptFields);
                    if ((string)definition["m_Namespace"] != "LevelEditorStub"
                        || Path.GetFileNameWithoutExtension((string)definition["m_AssemblyName"]) != "LevelEditorStub")
                        throw new IOException("Catalog script belongs to an unsupported assembly or namespace");
                    identity = (string)definition["m_ClassName"];
                    _scripts.Add(pointer.Id, identity);
                }
                if (obj.Type.ClassId != 114 || identity != expectedScript) throw new IOException("Catalog scalar object has an unexpected script type");
            }
            return values;
        }

        private static object ReadValue(BinaryReader reader, Field[] fields, int index, Kind kind)
        {
            Field field = fields[index];
            object result;
            if (kind == Kind.Text)
            {
                if (field.Type != "string") throw new IOException("Unexpected catalog string type");
                result = Text(reader);
            }
            else if (kind == Kind.Integer || kind == Kind.Float)
            {
                if (field.Size != 4 || field.End != index + 1
                    || (kind == Kind.Float ? field.Type != "float" : field.Type != "int" && field.Type != "SInt32"))
                    throw new IOException("Unexpected catalog number type");
                if (kind == Kind.Float) result = reader.ReadSingle(); else result = reader.ReadInt32();
            }
            else if (kind == Kind.Pointer) result = ReadPointer(reader, fields, index);
            else
            {
                int data = ArrayData(fields, index);
                int count = Count(reader, MaxItems);
                if (kind == Kind.Texts)
                {
                    string[] array = new string[count];
                    for (int i = 0; i < count; i++) array[i] = (string)ReadValue(reader, fields, data, Kind.Text);
                    result = array;
                }
                else
                {
                    Pointer[] array = new Pointer[count];
                    for (int i = 0; i < count; i++) array[i] = ReadPointer(reader, fields, data);
                    result = array;
                }
                if ((fields[index + 1].Meta & 0x4000) != 0) Align(reader);
            }
            if ((field.Meta & 0x4000) != 0) Align(reader);
            return result;
        }

        private static Pointer ReadPointer(BinaryReader reader, Field[] fields, int index)
        {
            if (fields[index].End != index + 3 || fields[index + 1].Size != 4 || fields[index + 2].Size != 8)
                throw new IOException("Unexpected catalog pointer layout");
            Pointer result = new Pointer { File = reader.ReadInt32(), Id = reader.ReadInt64() };
            if ((fields[index].Meta & 0x4000) != 0) Align(reader);
            return result;
        }

        private static int ArrayData(Field[] fields, int index)
        {
            int array = index + 1;
            if (array >= fields[index].End || (fields[array].Flags & 1) == 0 || fields[array].End != fields[index].End
                || array + 2 >= fields[array].End || fields[array + 1].Size != 4
                || fields[array + 1].End != array + 2 || fields[array + 2].End != fields[array].End)
                throw new IOException("Unexpected catalog array layout");
            return array + 2;
        }

        private static void SkipField(BinaryReader reader, Field[] fields, int index)
        {
            Field field = fields[index];
            if (field.Type == "string") { int count = Count(reader, MaxObject); SkipBytes(reader, count); Align(reader); }
            else if (index + 1 < field.End && (fields[index + 1].Flags & 1) != 0)
            {
                int data = ArrayData(fields, index), count = Count(reader, MaxObject);
                if (fields[data].End == data + 1 && fields[data].Size >= 0 && (fields[data].Meta & 0x4000) == 0)
                    SkipBytes(reader, checked(count * fields[data].Size));
                else for (int i = 0; i < count; i++) SkipField(reader, fields, data);
                if ((fields[index + 1].Meta & 0x4000) != 0) Align(reader);
            }
            else if (field.End > index + 1)
                for (int child = index + 1; child < field.End; child = fields[child].End) SkipField(reader, fields, child);
            else if (field.Size >= 0) SkipBytes(reader, field.Size);
            else throw new IOException("Unsupported variable catalog field");
            if ((field.Meta & 0x4000) != 0) Align(reader);
        }

        private static Field[] ReadTree(BinaryReader reader)
        {
            int count = Count(reader, 65536), stringSize = Count(reader, MaxMetadata);
            if (count == 0 || (long)count * 24 + stringSize > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new IOException("Invalid catalog type tree extent");
            Field[] fields = new Field[count];
            uint[] names = new uint[count], types = new uint[count];
            for (int i = 0; i < count; i++)
            {
                reader.ReadInt16();
                int depth = reader.ReadByte(), flags = reader.ReadByte();
                types[i] = reader.ReadUInt32(); names[i] = reader.ReadUInt32();
                int size = reader.ReadInt32(); reader.ReadInt32(); int meta = reader.ReadInt32();
                if (depth > 64 || (i == 0 ? depth != 0 : depth == 0 || depth > fields[i - 1].Depth + 1))
                    throw new IOException("Invalid catalog type tree depth");
                fields[i] = new Field { Depth = depth, Flags = flags, Size = size, Meta = meta, End = count };
            }
            byte[] strings = reader.ReadBytes(stringSize);
            Stack<int> stack = new Stack<int>();
            for (int i = 0; i < count; i++)
            {
                while (stack.Count != 0 && fields[stack.Peek()].Depth >= fields[i].Depth) fields[stack.Pop()].End = i;
                stack.Push(i);
                fields[i].Name = TreeString(strings, names[i]);
                fields[i].Type = TreeString(strings, types[i]);
            }
            return fields;
        }

        private static string TreeString(byte[] strings, uint offset)
        {
            if ((offset & 0x80000000) != 0)
            {
                switch (offset & 0x7fffffff)
                {
                    case 49: return "Array";
                    case 161: return "float";
                    case 222: return "int";
                    case 427: return "m_Name";
                    case 490: return "m_Script";
                    case 807: return "SInt32";
                    case 840: return "string";
                    default: return "#" + (offset & 0x7fffffff);
                }
            }
            if (offset >= strings.Length) throw new IOException("Invalid catalog type tree string offset");
            int end = (int)offset;
            while (end < strings.Length && strings[end] != 0 && end - offset <= 4096) end++;
            if (end == strings.Length || end - offset > 4096) throw new IOException("Invalid catalog type tree string");
            return Utf8.GetString(strings, (int)offset, end - (int)offset);
        }

        private static int Big32(byte[] data, int offset)
        {
            uint value = (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
            if (value > int.MaxValue) throw new IOException("Catalog header size exceeds limits");
            return (int)value;
        }
        private static int Count(BinaryReader reader, int limit)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > limit) throw new IOException("Catalog count exceeds limits");
            return count;
        }
        private static void SkipBytes(BinaryReader reader, int count)
        {
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new IOException("Truncated catalog field");
            reader.BaseStream.Position += count;
        }
        private static void Align(BinaryReader reader)
        {
            int padding = (int)((4 - reader.BaseStream.Position % 4) % 4);
            SkipBytes(reader, padding);
        }
        private static string CString(BinaryReader reader, int limit)
        {
            List<byte> bytes = new List<byte>();
            byte value;
            while ((value = reader.ReadByte()) != 0)
            {
                if (bytes.Count == limit) throw new IOException("Catalog string exceeds limits");
                bytes.Add(value);
            }
            return Utf8.GetString(bytes.ToArray());
        }
        private static string Text(BinaryReader reader)
        {
            int length = Count(reader, MaxObject);
            if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw new IOException("Truncated catalog string");
            string text = Utf8.GetString(reader.ReadBytes(length));
            Align(reader);
            return text;
        }
    }
}
