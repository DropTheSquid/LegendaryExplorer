﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ME3Explorer.Packages;
using System.Collections.ObjectModel;
using System.Collections;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using StreamHelpers;
using PropertyInfo = ME3Explorer.Packages.PropertyInfo;

namespace ME3Explorer.Unreal
{
    public class PropertyCollection : ObservableCollection<UProperty>
    {
        static readonly ConcurrentDictionary<string, PropertyCollection> defaultStructValuesME3 = new ConcurrentDictionary<string, PropertyCollection>();
        static readonly ConcurrentDictionary<string, PropertyCollection> defaultStructValuesME2 = new ConcurrentDictionary<string, PropertyCollection>();
        static readonly ConcurrentDictionary<string, PropertyCollection> defaultStructValuesME1 = new ConcurrentDictionary<string, PropertyCollection>();

        public int endOffset;

        /// <summary>
        /// Gets the UProperty with the specified name, returns null if not found. The property name is checked case insensitively. 
        /// Ensure the generic type matches the result you want or you will receive a null object back.
        /// </summary>
        /// <param name="name">Name of property to find</param>
        /// <returns>specified UProperty or null if not found</returns>
        public T GetProp<T>(string name) where T : UProperty
        {
            foreach (var prop in this)
            {
                if (prop.Name.Name != null && string.Equals(prop.Name.Name, name, StringComparison.CurrentCultureIgnoreCase))
                {
                    return prop as T;
                }
            }
            return null;
        }

        public bool TryReplaceProp(UProperty prop)
        {
            for (int i = 0; i < this.Count; i++)
            {
                if (this[i].Name.Name == prop.Name.Name)
                {
                    this[i] = prop;
                    return true;
                }
            }
            return false;
        }

        public void AddOrReplaceProp(UProperty prop)
        {
            if (!TryReplaceProp(prop))
            {
                this.Add(prop);
            }
        }

        public void WriteTo(Stream stream, IMEPackage pcc, bool requireNoneAtEnd = true)
        {
            foreach (var prop in this)
            {
                prop.WriteTo(stream, pcc);
            }
            if (requireNoneAtEnd && (Count == 0 || !(this.Last() is NoneProperty)))
            {
                stream.WriteNoneProperty(pcc);
            }
        }

        /// <summary>
        /// Checks if a property with the specified name exists in this property collection
        /// </summary>
        /// <param name="name">Name of property to find. If an empty name is passed in, any property without a name will cause this to return true.</param>
        /// <returns>True if property is found, false if list is empty or not found</returns>
        public bool ContainsNamedProp(NameReference name)
        {
            return Count > 0 && this.Any(x => x.Name == name);
        }

        public static PropertyCollection ReadProps(IMEPackage pcc, MemoryStream stream, string typeName, bool includeNoneProperty = false, bool requireNoneAtEnd = true, IEntry entry = null)
        {
            //Uncomment this for debugging property engine
            /*DebugOutput.StartDebugger("Property Engine ReadProps() for "+typeName);
            if (pcc.FileName == "C:\\Users\\Dev\\Downloads\\ME2_Placeables.upk")
            {
              Debugger.Break();
            }*/

            PropertyCollection props = new PropertyCollection();
            long startPosition = stream.Position;
            try
            {
                while (stream.Position + 8 <= stream.Length)
                {
                    long propertyStartPosition = stream.Position;
                    int nameIdx = stream.ReadInt32();
                    if (!pcc.isName(nameIdx))
                    {
                        stream.Seek(-4, SeekOrigin.Current);
                        break;
                    }
                    string name = pcc.getNameEntry(nameIdx);
                    if (name == "None")
                    {
                        props.Add(new NoneProperty(stream) { StartOffset = propertyStartPosition, ValueOffset = propertyStartPosition });
                        stream.Seek(4, SeekOrigin.Current);
                        break;
                    }
                    NameReference nameRef = new NameReference (name, stream.ReadInt32());
                    int typeIdx = stream.ReadInt32();
                    stream.Seek(4, SeekOrigin.Current);
                    int size = stream.ReadInt32();
                    if (!pcc.isName(typeIdx) || size < 0 || size > stream.Length - stream.Position)
                    {
                        stream.Seek(-16, SeekOrigin.Current);
                        break;
                    }
                    stream.Seek(4, SeekOrigin.Current);
                    PropertyType type;
                    string namev = pcc.getNameEntry(typeIdx);
                    //Debug.WriteLine("Reading " + name + " (" + namev + ") at 0x" + (stream.Position - 24).ToString("X8"));
                    if (Enum.IsDefined(typeof(PropertyType), namev))
                    {
                        Enum.TryParse(namev, out type);
                    }
                    else
                    {
                        type = PropertyType.Unknown;
                    }
                    switch (type)
                    {
                        case PropertyType.StructProperty:
                            string structType = pcc.getNameEntry(stream.ReadInt32());
                            stream.Seek(4, SeekOrigin.Current);
                            long valOffset = stream.Position;
                            if (UnrealObjectInfo.isImmutable(structType, pcc.Game))
                            {
                                PropertyCollection structProps = ReadImmutableStruct(pcc, stream, structType, size, entry);
                                props.Add(new StructProperty(structType, structProps, nameRef, true) { StartOffset = propertyStartPosition, ValueOffset = valOffset });
                            }
                            else
                            {
                                PropertyCollection structProps = ReadProps(pcc, stream, structType, includeNoneProperty, entry: entry);
                                props.Add(new StructProperty(structType, structProps, nameRef) { StartOffset = propertyStartPosition, ValueOffset = valOffset });
                            }
                            break;
                        case PropertyType.IntProperty:
                            IntProperty ip = new IntProperty(stream, nameRef);
                            ip.StartOffset = propertyStartPosition;
                            props.Add(ip);
                            break;
                        case PropertyType.FloatProperty:
                            props.Add(new FloatProperty(stream, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.ObjectProperty:
                            props.Add(new ObjectProperty(stream, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.NameProperty:
                            props.Add(new NameProperty(stream, pcc, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.BoolProperty:
                            props.Add(new BoolProperty(stream, pcc.Game, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.BioMask4Property:
                            props.Add(new BioMask4Property(stream, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.ByteProperty:
                            {
                                if (size != 1)
                                {
                                    NameReference enumType;
                                    if (pcc.Game == MEGame.ME3 || pcc.Game == MEGame.UDK)
                                    {
                                        enumType = new NameReference(pcc.getNameEntry(stream.ReadInt32()), stream.ReadInt32());
                                    }
                                    else
                                    {
                                        //Debug.WriteLine("Reading enum for ME1/ME2 at 0x" + propertyStartPosition.ToString("X6"));

                                        //Attempt to get info without lookup first
                                        var enumname = UnrealObjectInfo.GetEnumType(pcc.Game, name, typeName);
                                        ClassInfo classInfo = null;
                                        if (enumname == null)
                                        {
                                            if (entry != null)
                                            {
                                                if (entry.FileRef.Game == MEGame.ME1)
                                                {
                                                    classInfo = ME1Explorer.Unreal.ME1UnrealObjectInfo.generateClassInfo((IExportEntry)entry);
                                                }
                                                if (entry.FileRef.Game == MEGame.ME2)
                                                {
                                                    classInfo = ME2Explorer.Unreal.ME2UnrealObjectInfo.generateClassInfo((IExportEntry)entry);
                                                }
                                            }
                                        }

                                        //Use DB info or attempt lookup
                                        enumType = new NameReference(enumname ?? UnrealObjectInfo.GetEnumType(pcc.Game, name, typeName, classInfo));
                                    }
                                    try
                                    {
                                        props.Add(new EnumProperty(stream, pcc, enumType, nameRef) { StartOffset = propertyStartPosition });
                                    }
                                    catch (Exception)
                                    {
                                        //ERROR
                                        //Debugger.Break();
                                        var unknownEnum = new UnknownProperty(stream, 0, enumType, nameRef) { StartOffset = propertyStartPosition };
                                        props.Add(unknownEnum);
                                    }
                                }
                                else
                                {
                                    if (pcc.Game == MEGame.ME3)
                                    {
                                        stream.Seek(8, SeekOrigin.Current);
                                    }
                                    props.Add(new ByteProperty(stream, nameRef) { StartOffset = propertyStartPosition });
                                }
                            }
                            break;
                        case PropertyType.ArrayProperty:
                            {
                                //Debug.WriteLine("Reading array properties, starting at 0x" + stream.Position.ToString("X5"));
                                UProperty ap = ReadArrayProperty(stream, pcc, typeName, nameRef, IncludeNoneProperties: includeNoneProperty, parsingEntry: entry);
                                ap.StartOffset = propertyStartPosition;
                                props.Add(ap);
                            }
                            break;
                        case PropertyType.StrProperty:
                            {
                                props.Add(new StrProperty(stream, nameRef) { StartOffset = propertyStartPosition });
                            }
                            break;
                        case PropertyType.StringRefProperty:
                            props.Add(new StringRefProperty(stream, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.DelegateProperty:
                            props.Add(new DelegateProperty(stream, pcc, nameRef) { StartOffset = propertyStartPosition });
                            break;
                        case PropertyType.Unknown:
                            {
                                // Debugger.Break();
                                props.Add(new UnknownProperty(stream, size, pcc.getNameEntry(typeIdx), nameRef) { StartOffset = propertyStartPosition });
                            }
                            break;
                        case PropertyType.None:
                            if (includeNoneProperty)
                            {
                                props.Add(new NoneProperty(stream) { StartOffset = propertyStartPosition });
                            }
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception: " + e.Message);
            }
            if (props.Count > 0)
            {
                //error reading props.
                if (props[props.Count - 1].PropType != PropertyType.None && requireNoneAtEnd)
                {
                    if (entry != null)
                    {
                        Debug.WriteLine(entry.UIndex + " " + entry.ObjectName + " - Invalid properties: Does not end with None");
                    }
#if DEBUG
                    props.endOffset = (int)stream.Position;
                    return props;
#else
                    stream.Seek(startPosition, SeekOrigin.Begin);
                    return new PropertyCollection { endOffset = (int)stream.Position };
#endif
                }
                //remove None Property
                if (!includeNoneProperty)
                {
                    props.RemoveAt(props.Count - 1);
                }
            }
            props.endOffset = (int)stream.Position;
            return props;
        }

        public static PropertyCollection ReadImmutableStruct(IMEPackage pcc, MemoryStream stream, string structType, int size, IEntry parsingEntry = null)
        {
            PropertyCollection props = new PropertyCollection();
            switch (pcc.Game)
            {
                case MEGame.ME3 when ME3UnrealObjectInfo.Structs.ContainsKey(structType):
                {
                    bool stripTransients = true;
                    if (parsingEntry != null && parsingEntry.ClassName == "Class")
                    {
                        stripTransients = false;
                    }
                    PropertyCollection defaultProps;
                    //cache
                    if (defaultStructValuesME3.ContainsKey(structType) && stripTransients)
                    {
                        defaultProps = defaultStructValuesME3[structType];
                    }
                    else
                    {
                        defaultProps = ME3UnrealObjectInfo.getDefaultStructValue(structType, stripTransients);
                        if (defaultProps == null)
                        {
                            long startPos = stream.Position;
                            props.Add(new UnknownProperty(stream, size) { StartOffset = startPos });
                            return props;
                        }
                        if (stripTransients)
                        {
                            defaultStructValuesME3.TryAdd(structType, defaultProps);
                        }
                    }
                    foreach (var prop in defaultProps)
                    {
                        UProperty uProperty = null;
                        if (prop is StructProperty defaultStructProperty)
                        {
                            //Set correct struct type
                            uProperty = ReadImmutableStructProp(pcc, stream, prop, structType, defaultStructProperty.StructType);
                        }
                        else
                        {
                            uProperty = ReadImmutableStructProp(pcc, stream, prop, structType);
                        }

                        if (uProperty.PropType != PropertyType.None)
                        {
                            props.Add(uProperty);
                        }
                    }
                    return props;
                }

                case MEGame.ME2 when ME2Explorer.Unreal.ME2UnrealObjectInfo.Structs.ContainsKey(structType):
                {
                    PropertyCollection defaultProps;
                    bool stripTransients = true;
                    if (parsingEntry != null && parsingEntry.ClassName == "Class")
                    {
                        stripTransients = false;
                    }
                    //Cache
                    if (defaultStructValuesME2.ContainsKey(structType) && stripTransients)
                    {
                        defaultProps = defaultStructValuesME2[structType];
                    }
                    else
                    {
                        Debug.WriteLine("Build&cache for ME2 struct: " + structType);
                        defaultProps = ME2Explorer.Unreal.ME2UnrealObjectInfo.getDefaultStructValue(structType, stripTransients);
                        if (defaultProps == null)
                        {
                            long pos = stream.Position;
                            props.Add(new UnknownProperty(stream, size) { StartOffset = pos });
                            return props;
                        }
                        if (stripTransients)
                        {
                            defaultStructValuesME2.TryAdd(structType, defaultProps);
                        }
                    }
                    //Debug.WriteLine("ME2: Build immuatable struct properties for struct type " + structType);
                    foreach (var prop in defaultProps)
                    {
                        UProperty uProperty = ReadImmutableStructProp(pcc, stream, prop, structType);
                        //Debug.WriteLine("  >> ME2: Built immutable property: " + uProperty.Name + " at 0x" + uProperty.StartOffset.ToString("X5"));
                        if (uProperty.PropType != PropertyType.None)
                        {
                            props.Add(uProperty);
                        }

                    }
                    return props;
                }

                case MEGame.ME1 when ME1Explorer.Unreal.ME1UnrealObjectInfo.Structs.ContainsKey(structType):
                {
                    PropertyCollection defaultProps;
                    bool stripTransients = true;
                    if (parsingEntry != null && parsingEntry.ClassName == "Class")
                    {
                        stripTransients = false;
                    }
                    //Cache
                    if (defaultStructValuesME1.ContainsKey(structType) && stripTransients)
                    {
                        defaultProps = defaultStructValuesME1[structType];
                    }
                    else
                    {
                        Debug.WriteLine("Build&cache for ME1 struct: " + structType);
                        defaultProps = ME1Explorer.Unreal.ME1UnrealObjectInfo.getDefaultStructValue(structType, stripTransients);
                        if (defaultProps == null)
                        {
                            long pos = stream.Position;
                            props.Add(new UnknownProperty(stream, size) { StartOffset = pos });
                            return props;
                        }
                        if (stripTransients)
                        {
                            defaultStructValuesME1.TryAdd(structType, defaultProps);
                        }
                    }
                    //Debug.WriteLine("ME1: Build immuatable struct properties for struct type " + structType);
                    foreach (var prop in defaultProps)
                    {
                        //Debug.WriteLine("  > ME1: Building immutable property: " + prop.Name + " at 0x" + stream.Position.ToString("X5"));

                        UProperty uProperty = ReadImmutableStructProp(pcc, stream, prop, structType);
                        //Debug.WriteLine("  >> ME1: Built immutable property: " + uProperty.Name + " at 0x" + uProperty.StartOffset.ToString("X5"));
                        if (uProperty.PropType != PropertyType.None)
                        {
                            props.Add(uProperty);
                        }

                    }
                    return props;
                }
            }

            Debug.WriteLine("Unknown struct type: " + structType);
            props.Add(new UnknownProperty(stream, size) { StartOffset = stream.Position });
            return props;
        }

        //Nested struct type is for structs in structs
        static UProperty ReadImmutableStructProp(IMEPackage pcc, MemoryStream stream, UProperty template, string structType, string nestedStructType = null)
        {
            if (stream.Position + 1 >= stream.Length)
            {
                throw new EndOfStreamException("tried to read past bounds of Export Data");
            }
            long startPos = stream.Position;

            switch (template.PropType)
            {
                case PropertyType.FloatProperty:
                    return new FloatProperty(stream, template.Name) { StartOffset = startPos };
                case PropertyType.IntProperty:
                    return new IntProperty(stream, template.Name) { StartOffset = startPos };
                case PropertyType.ObjectProperty:
                    return new ObjectProperty(stream, template.Name) { StartOffset = startPos };
                case PropertyType.StringRefProperty:
                    return new StringRefProperty(stream, template.Name) { StartOffset = startPos };
                case PropertyType.NameProperty:
                    return new NameProperty(stream, pcc, template.Name) { StartOffset = startPos };
                case PropertyType.BoolProperty:
                    //always say it's ME3 so that bools get read as 1 byte
                    return new BoolProperty(stream, pcc.Game, template.Name, true) { StartOffset = startPos };
                case PropertyType.ByteProperty:
                    if (template is EnumProperty)
                    {
                        string enumType = UnrealObjectInfo.GetEnumType(pcc.Game, template.Name, structType);
                        return new EnumProperty(stream, pcc, enumType, template.Name) { StartOffset = startPos };
                    }
                    return new ByteProperty(stream, template.Name) { StartOffset = startPos };
                case PropertyType.BioMask4Property:
                    return new BioMask4Property(stream, template.Name) { StartOffset = startPos };
                case PropertyType.StrProperty:
                    return new StrProperty(stream, template.Name) { StartOffset = startPos };
                case PropertyType.ArrayProperty:
                    var arrayProperty = ReadArrayProperty(stream, pcc, structType, template.Name, true);
                    arrayProperty.StartOffset = startPos;
                    return arrayProperty;//this implementation needs checked, as I am not 100% sure of it's validity.
                case PropertyType.StructProperty:
                    long valuePos = stream.Position;
                    PropertyCollection structProps = ReadImmutableStruct(pcc, stream, UnrealObjectInfo.GetPropertyInfo(pcc.Game, template.Name, structType).reference, 0);
                    var structProp = new StructProperty(nestedStructType ?? structType, structProps, template.Name, true);
                    structProp.StartOffset = startPos;
                    structProp.ValueOffset = valuePos;
                    return structProp;//this implementation needs checked, as I am not 100% sure of it's validity.
                case PropertyType.None:
                    return new NoneProperty() { StartOffset = startPos };
                case PropertyType.DelegateProperty:
                    throw new NotImplementedException("cannot read Delegate property of Immutable struct");
                case PropertyType.Unknown:
                    throw new NotImplementedException("cannot read Unknown property of Immutable struct");
            }
            throw new NotImplementedException("cannot read Unknown property of Immutable struct");
        }

        public static UProperty ReadArrayProperty(MemoryStream stream, IMEPackage pcc, string enclosingType, NameReference name, bool IsInImmutable = false, bool IncludeNoneProperties = false, IEntry parsingEntry = null)
        {
            long arrayOffset = IsInImmutable ? stream.Position : stream.Position - 24;
            ArrayType arrayType = UnrealObjectInfo.GetArrayType(pcc.Game, name, enclosingType, parsingEntry);
            //Debug.WriteLine("Reading array length at 0x" + stream.Position.ToString("X5"));
            int count = stream.ReadInt32();
            switch (arrayType)
            {
                case ArrayType.Object:
                    {
                        var props = new List<ObjectProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new ObjectProperty(stream) { StartOffset = startPos });
                        }
                        return new ArrayProperty<ObjectProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Name:
                    {
                        var props = new List<NameProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new NameProperty(stream, pcc) { StartOffset = startPos });
                        }
                        return new ArrayProperty<NameProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Enum:
                    {
                        //Attempt to get info without lookup first
                        var enumname = UnrealObjectInfo.GetEnumType(pcc.Game, name, enclosingType);
                        ClassInfo classInfo = null;
                        if (enumname == null)
                        {
                            if (parsingEntry != null)
                            {
                                if (parsingEntry.FileRef.Game == MEGame.ME1)
                                {
                                    classInfo = ME1Explorer.Unreal.ME1UnrealObjectInfo.generateClassInfo((IExportEntry)parsingEntry);
                                }
                                if (parsingEntry.FileRef.Game == MEGame.ME2)
                                {
                                    classInfo = ME2Explorer.Unreal.ME2UnrealObjectInfo.generateClassInfo((IExportEntry)parsingEntry);
                                }
                            }
                        }

                        //Use DB info or attempt lookup
                        NameReference enumType = new NameReference(enumname ?? UnrealObjectInfo.GetEnumType(pcc.Game, name, enclosingType, classInfo));

                        var props = new List<EnumProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new EnumProperty(stream, pcc, enumType) { StartOffset = startPos });
                        }
                        return new ArrayProperty<EnumProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Struct:
                    {
                        long startPos = stream.Position;

                        var props = new List<StructProperty>();
                        var propertyInfo = UnrealObjectInfo.GetPropertyInfo(pcc.Game, name, enclosingType);
                        if (propertyInfo == null && parsingEntry != null)
                        {
                            ClassInfo currentInfo;
                            switch (parsingEntry.FileRef.Game)
                            {
                                case MEGame.ME1:
                                    currentInfo = ME1Explorer.Unreal.ME1UnrealObjectInfo.generateClassInfo(parsingEntry as IExportEntry);
                                    break;
                                case MEGame.ME2:
                                    currentInfo = ME2Explorer.Unreal.ME2UnrealObjectInfo.generateClassInfo(parsingEntry as IExportEntry);
                                    break;
                                case MEGame.ME3:
                                default:
                                    currentInfo = ME3UnrealObjectInfo.generateClassInfo(parsingEntry as IExportEntry);
                                    break;
                            }
                            currentInfo.baseClass = ((IExportEntry)parsingEntry).ClassParent;
                            propertyInfo = UnrealObjectInfo.GetPropertyInfo(pcc.Game, name, enclosingType, currentInfo);
                        }

                        string arrayStructType = propertyInfo?.reference;
                        if (IsInImmutable || UnrealObjectInfo.isImmutable(arrayStructType, pcc.Game))
                        {
                            int arraySize = 0;
                            if (!IsInImmutable)
                            {
                                stream.Seek(-16, SeekOrigin.Current);
                                //Debug.WriteLine("Arraysize at 0x" + stream.Position.ToString("X5"));
                                arraySize = stream.ReadInt32();
                                stream.Seek(12, SeekOrigin.Current);
                            }
                            for (int i = 0; i < count; i++)
                            {
                                long offset = stream.Position;
                                try
                                {
                                    PropertyCollection structProps = ReadImmutableStruct(pcc, stream, arrayStructType, arraySize / count, parsingEntry: parsingEntry);
                                    props.Add(new StructProperty(arrayStructType, structProps, isImmutable: true)
                                    {
                                        StartOffset = offset,
                                        ValueOffset = offset
                                    });
                                }
                                catch (Exception e)
                                {
                                    Debug.WriteLine("ERROR READING ARRAY PROP");
                                    return new ArrayProperty<StructProperty>(arrayOffset, props, name);
                                }
                            }
                        }
                        else
                        {
                            for (int i = 0; i < count; i++)
                            {
                                long structOffset = stream.Position;
                                //Debug.WriteLine("reading array struct: " + arrayStructType + " at 0x" + stream.Position.ToString("X5"));
                                PropertyCollection structProps = ReadProps(pcc, stream, arrayStructType, includeNoneProperty: IncludeNoneProperties,entry: parsingEntry);
#if DEBUG
                                try
                                {
#endif
                                    props.Add(new StructProperty(arrayStructType, structProps)
                                    {
                                        StartOffset = structOffset,
                                        ValueOffset = structProps[0].StartOffset
                                    });
#if DEBUG
                                }
                                catch (Exception e)
                                {
                                    return new ArrayProperty<StructProperty>(arrayOffset, props, name);
                                }
#endif
                            }
                        }
                        return new ArrayProperty<StructProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Bool:
                    {
                        var props = new List<BoolProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new BoolProperty(stream, pcc.Game, isArrayContained: true) { StartOffset = startPos });
                        }
                        return new ArrayProperty<BoolProperty>(arrayOffset, props, name);
                    }
                case ArrayType.String:
                    {
                        var props = new List<StrProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new StrProperty(stream) { StartOffset = startPos });
                        }
                        return new ArrayProperty<StrProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Float:
                    {
                        var props = new List<FloatProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new FloatProperty(stream) { StartOffset = startPos });
                        }
                        return new ArrayProperty<FloatProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Byte:
                    {
                        var props = new List<ByteProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new ByteProperty(stream) { StartOffset = startPos });
                        }
                        return new ArrayProperty<ByteProperty>(arrayOffset, props, name);
                    }
                case ArrayType.Int:
                default:
                    {
                        var props = new List<IntProperty>();
                        for (int i = 0; i < count; i++)
                        {
                            long startPos = stream.Position;
                            props.Add(new IntProperty(stream) { StartOffset = startPos });
                        }
                        return new ArrayProperty<IntProperty>(arrayOffset, props, name);
                    }
            }
        }

    }

    public abstract class UProperty : NotifyPropertyChangedBase
    {
        public PropertyType PropType;
        private NameReference _name;
        /// <summary>
        /// Offset to the value for this property - note not all properties have actual values.
        /// </summary>
        public long ValueOffset;

        /// <summary>
        /// Offset to the start of this property as it was read by PropertyCollection.ReadProps()
        /// </summary>
        public long StartOffset { get; set; }

        public NameReference Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        protected UProperty(NameReference? name)
        {
            _name = name ?? new NameReference();
        }

        public abstract void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false);

        /// <summary>
        /// Gets the length of this property in bytes. Do not use this if this is an ArrayProperty child object.
        /// </summary>
        /// <param name="pcc"></param>
        /// <param name="valueOnly"></param>
        /// <returns></returns>
        public long GetLength(IMEPackage pcc, bool valueOnly = false)
        {
            var stream = new MemoryStream();
            WriteTo(stream, pcc, valueOnly);
            return stream.Length;
        }
    }

    [DebuggerDisplay("NoneProperty")]
    public class NoneProperty : UProperty
    {
        public NoneProperty() : base("None")
        {
            PropType = PropertyType.None;
        }

        public NoneProperty(MemoryStream stream) : this()
        {
            ValueOffset = stream.Position;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteNoneProperty(pcc);
            }
        }
    }

    [DebuggerDisplay("StructProperty | {Name.Name} - {StructType}")]
    public class StructProperty : UProperty
    {
        public readonly bool IsImmutable;

        public string StructType { get; }
        public PropertyCollection Properties { get; set; }

        public StructProperty(string structType, PropertyCollection props, NameReference? name = null, bool isImmutable = false) : base(name)
        {
            StructType = structType;
            Properties = props;
            IsImmutable = isImmutable;
            PropType = PropertyType.StructProperty;
        }

        public StructProperty(string structType, bool isImmutable, params UProperty[] props) : base(null)
        {
            StructType = structType;
            IsImmutable = isImmutable;
            PropType = PropertyType.StructProperty;
            Properties = new PropertyCollection();
            foreach (var prop in props)
            {
                Properties.Add(prop);
            }

        }

        public T GetProp<T>(string name) where T : UProperty
        {
            return Properties.GetProp<T>(name);
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (valueOnly)
            {
                foreach (var prop in Properties)
                {
                    //Debug.WriteLine("Writing struct prop " + prop.Name + " at 0x" + stream.Position.ToString("X4"));
                    prop.WriteTo(stream, pcc, IsImmutable);
                }
                if (!IsImmutable && (Properties.Count == 0 || !(Properties.Last() is NoneProperty)))
                {
                    stream.WriteNoneProperty(pcc);
                }
            }
            else
            {
                stream.WriteStructProperty(pcc, Name, StructType, () =>
                {
                    MemoryStream m = new MemoryStream();
                    foreach (var prop in Properties)
                    {
                        prop.WriteTo(m, pcc, IsImmutable);
                    }

                    if (!IsImmutable && (Properties.Count == 0 || !(Properties.Last() is NoneProperty))) //ensure ending none
                    {
                        m.WriteNoneProperty(pcc);
                    }
                    return m;
                });
            }
        }

        /// <summary>
        /// EXPERIMENTAL - USE WITH CAUTION - ONLY WORKS FOR ME3
        /// </summary>
        public T GetStruct<T>() where T : class, new()
        {
            T uStruct = new T();
            MethodInfo getPropMethodInfo = this.GetType().GetMethod(nameof(GetProp));
            if (typeof(T).Name != StructType)
            {
                throw new NotSupportedException($"{typeof(T).Name} does not match the StructProperty's struct type: {StructType}");
            }

            if (!ME3UnrealObjectInfo.Structs.TryGetValue(StructType, out ClassInfo classInfo))
            {
                throw new ArgumentException($"{StructType} is not a recognized struct!");
            }
            FieldInfo[] fields = typeof(T).GetFields();
            foreach (FieldInfo info in fields)
            {
                if (classInfo.properties.TryGetValue(info.Name, out PropertyInfo propInfo)
                 && getPropMethodInfo.MakeGenericMethod(getUPropertyType(propInfo)).Invoke(this, new object[]{ info.Name }) is UProperty uProp)
                {
                    info.SetValue(uStruct, getUPropertyValue(uProp, propInfo));
                }
            }

            return uStruct;

            Type getUPropertyType(PropertyInfo propInfo)
            {
                switch (propInfo.type)
                {
                    case PropertyType.StructProperty:
                        return typeof(StructProperty);
                    case PropertyType.IntProperty:
                        return typeof(IntProperty);
                    case PropertyType.FloatProperty:
                        return typeof(FloatProperty);
                    case PropertyType.DelegateProperty:
                        return typeof(DelegateProperty);
                    case PropertyType.ObjectProperty:
                        return typeof(ObjectProperty);
                    case PropertyType.NameProperty:
                        return typeof(NameProperty);
                    case PropertyType.BoolProperty:
                        return typeof(BoolProperty);
                    case PropertyType.BioMask4Property:
                        return typeof(BioMask4Property);
                    case PropertyType.ByteProperty when propInfo.IsEnumProp():
                        return typeof(EnumProperty);
                    case PropertyType.ByteProperty:
                        return typeof(ByteProperty);
                    case PropertyType.ArrayProperty:
                    {
                        if (Enum.TryParse(propInfo.reference, out PropertyType arrayType))
                        {
                            return typeof(ArrayProperty<>).MakeGenericType(getUPropertyType(new PropertyInfo {type = arrayType }));
                        }
                        if (ME3UnrealObjectInfo.Classes.ContainsKey(propInfo.reference))
                        {
                            return typeof(ArrayProperty<ObjectProperty>);
                        }
                        return typeof(ArrayProperty<StructProperty>);
                    }
                    case PropertyType.StrProperty:
                        return typeof(StrProperty);
                    case PropertyType.StringRefProperty:
                        return typeof(StringRefProperty);
                    case PropertyType.None:
                    case PropertyType.Unknown:
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            object getUPropertyValue(UProperty prop, PropertyInfo propInfo)
            {
                switch (prop)
                {
                    case ArrayPropertyBase arrayPropertyBase:
                    {
                        List<object> objVals = arrayPropertyBase.Properties.Select(p => getUPropertyValue(p, propInfo)).ToList();
                        Type arrayType = getArrayPropertyValueType(arrayPropertyBase, propInfo);
                        //IEnumerable<arrayType> typedEnumerable = objVals.Cast<arrayType>();
                        var typedEnumerable = typeof(Enumerable).InvokeGenericMethod(nameof(Enumerable.Cast), arrayType, null, objVals);
                        //return typedEnumerable.ToArray();
                        return typeof(Enumerable).InvokeGenericMethod(nameof(Enumerable.ToArray), arrayType, null, typedEnumerable);
                    }
                    case BioMask4Property bioMask4Property:
                        return bioMask4Property.Value;
                    case BoolProperty boolProperty:
                        return boolProperty.Value;
                    case ByteProperty byteProperty:
                        return byteProperty.Value;
                    case DelegateProperty delegateProperty:
                        return delegateProperty.unk;
                    case EnumProperty enumProperty:
                        var enumType = Type.GetType($"Unreal.ME3Enums.{propInfo.reference}");
                        return Enum.Parse(enumType, enumProperty.Value.InstancedString);
                    case FloatProperty floatProperty:
                        return floatProperty.Value;
                    case IntProperty intProperty:
                        return intProperty.Value;
                    case NameProperty nameProperty:
                        return nameProperty.Value;
                    case ObjectProperty objectProperty:
                        return objectProperty.Value;
                    case StringRefProperty stringRefProperty:
                        return stringRefProperty.Value;
                    case StrProperty strProperty:
                        return strProperty.Value;
                    case StructProperty structProperty:
                    {
                        Type structType = Type.GetType($"Unreal.ME3Structs.{propInfo.reference}");
                        //return structProperty.GetStruct<structType>();
                        return typeof(StructProperty).InvokeGenericMethod(nameof(structProperty.GetStruct), structType, structProperty);
                    }
                    case UnknownProperty unknownProperty:
                    case NoneProperty noneProperty:
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            Type getArrayPropertyValueType(ArrayPropertyBase arrProp, PropertyInfo propInfo)
            {
                switch (arrProp)
                {
                    case ArrayProperty<IntProperty> _:
                        return typeof(int);
                    case ArrayProperty<StringRefProperty> _:
                        return typeof(int);
                    case ArrayProperty<ObjectProperty> _:
                        return typeof(int);
                    case ArrayProperty<DelegateProperty> _:
                        return typeof(int);
                    case ArrayProperty<FloatProperty> _:
                        return typeof(float);
                    case ArrayProperty<BoolProperty> _:
                        return typeof(bool);
                    case ArrayProperty<StrProperty> _:
                        return typeof(string);
                    case ArrayProperty<ByteProperty> _:
                        return typeof(byte);
                    case ArrayProperty<BioMask4Property> _:
                        return typeof(byte);
                    case ArrayProperty<NameProperty> _:
                        return typeof(NameReference);
                    case ArrayProperty<EnumProperty> _:
                        return Type.GetType($"Unreal.ME3Enums.{propInfo.reference}");
                    case ArrayProperty<StructProperty> _:
                        return Type.GetType($"Unreal.ME3Structs.{propInfo.reference}");
                    default:
                        throw new  NotImplementedException();
                }
            }
        }
    }

    public static class StructArrayExtensions
    {
        /// <summary>
        /// EXPERIMENTAL - ONLY WORKS FOR ME3
        /// </summary>
        public static IEnumerable<T> AsStructs<T>(this ArrayProperty<StructProperty> arrayProperty) where T : class, new()
        {
            foreach (StructProperty structProperty in arrayProperty)
            {
                yield return structProperty.GetStruct<T>();
            }
        }
    }

    [DebuggerDisplay("IntProperty | {Name} = {Value}")]
    public class IntProperty : UProperty, IComparable
    {
        int _value;
        public int Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public IntProperty(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            Value = stream.ReadInt32();
            PropType = PropertyType.IntProperty;
        }

        public IntProperty(int val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.IntProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteIntProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteInt32(Value);
            }
        }

        public int CompareTo(object obj)
        {
            switch (obj)
            {
                case null:
                    return 1;
                case IntProperty otherInt:
                    return Value.CompareTo(otherInt.Value);
                default:
                    throw new ArgumentException("Cannot compare IntProperty to object that is not of type IntProperty.");
            }
        }

        public static implicit operator IntProperty(int n)
        {
            return new IntProperty(n);
        }

        public static implicit operator int(IntProperty p)
        {
            return p.Value;
        }
    }

    [DebuggerDisplay("FloatProperty | {Name} = {Value}")]
    public class FloatProperty : UProperty, IComparable
    {
        float _value;
        public float Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public FloatProperty(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            Value = stream.ReadFloat();
            PropType = PropertyType.FloatProperty;
        }

        public FloatProperty(float val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.FloatProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteFloatProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteFloat(Value);
            }
        }

        public int CompareTo(object obj)
        {
            switch (obj)
            {
                case null:
                    return 1;
                case FloatProperty otherFloat:
                    return Value.CompareTo(otherFloat.Value);
                default:
                    throw new ArgumentException("Cannot compare FloatProperty to object that is not of type FloatProperty.");
            }
        }

        public static implicit operator FloatProperty(float n)
        {
            return new FloatProperty(n);
        }

        public static implicit operator float(FloatProperty p)
        {
            return p.Value;
        }
    }

    [DebuggerDisplay("ObjectProperty | {Name} = {Value}")]
    public class ObjectProperty : UProperty, IComparable
    {
        int _value;
        public int Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public ObjectProperty(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            Value = stream.ReadInt32();
            PropType = PropertyType.ObjectProperty;
        }

        public ObjectProperty(int val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.ObjectProperty;
        }

        public ObjectProperty(IEntry referencedEntry, NameReference? name = null) : base(name)
        {
            Value = referencedEntry.UIndex;
            PropType = PropertyType.ObjectProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteObjectProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteInt32(Value);
            }
        }

        public int CompareTo(object obj)
        {
            switch (obj)
            {
                case null:
                    return 1;
                case ObjectProperty otherObj:
                    return Value.CompareTo(otherObj.Value);
                default:
                    throw new ArgumentException("Cannot compare ObjectProperty to object that is not of type ObjectProperty.");
            }
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ObjectProperty);
        }

        public bool Equals(ObjectProperty p)
        {
            // If parameter is null, return false.
            if (p is null)
            {
                return false;
            }

            // Optimization for a common success case.
            if (object.ReferenceEquals(this, p))
            {
                return true;
            }

            // If run-time types are not exactly the same, return false.
            if (this.GetType() != p.GetType())
            {
                return false;
            }

            // Return true if the fields match.
            // Note that the base class is not invoked because it is
            // System.Object, which defines Equals as reference equality.
            return (Value == p.Value);
        }
    }

    [DebuggerDisplay("NameProperty | {Name} = {Value}")]
    public class NameProperty : UProperty
    {
        NameReference _value;
        public NameReference Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public NameProperty(NameReference? propertyName = null, NameReference? value = null) : base(propertyName)
        {
            PropType = PropertyType.NameProperty;
            if (value is NameReference name)
            {
                Value = name;
            }
        }

        public NameProperty(MemoryStream stream, IMEPackage pcc, NameReference? propertyName = null) : base(propertyName)
        {
            ValueOffset = stream.Position;
            Value = new NameReference(pcc.getNameEntry(stream.ReadInt32()), stream.ReadInt32());
            PropType = PropertyType.NameProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteNameProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteInt32(pcc.FindNameOrAdd(Value.Name));
                stream.WriteInt32(Value.Number);
            }
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as NameProperty);
        }

        public bool Equals(NameProperty p)
        {
            // If parameter is null, return false.
            if (p is null)
            {
                return false;
            }

            // Optimization for a common success case.
            if (ReferenceEquals(this, p))
            {
                return true;
            }

            // If run-time types are not exactly the same, return false.
            if (GetType() != p.GetType())
            {
                return false;
            }

            // Return true if the fields match.
            // Note that the base class is not invoked because it is
            // System.Object, which defines Equals as reference equality.
            return Value == p.Value.Name && Value.Number == p.Value.Number;
        }

        public override string ToString()
        {
            return Value;
        }
    }

    [DebuggerDisplay("BoolProperty | {Name} = {Value}")]
    public class BoolProperty : UProperty
    {
        bool _value;
        public bool Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public BoolProperty(MemoryStream stream, MEGame game, NameReference? name = null, bool isArrayContained = false) : base(name)
        {
            ValueOffset = stream.Position;
            if (game != MEGame.ME3 && game != MEGame.UDK && isArrayContained)
            {
                //ME2 seems to read 1 byte... sometimes...
                //ME1 as well
                Value = stream.ReadBoolByte();
            }
            else
            {
                Value = (game == MEGame.ME3 || game == MEGame.UDK) ? stream.ReadBoolByte() : stream.ReadBoolInt();
            }
            PropType = PropertyType.BoolProperty;
        }

        public BoolProperty(bool val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.BoolProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteBoolProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteBoolByte(Value);

                //if (pcc.Game == MEGame.ME3 || isArrayContained)
                //{
                //    stream.WriteValueB8(Value);
                //}
                //else
                //{
                //    stream.WriteValueB32(Value);
                //}
            }
        }

        public static implicit operator BoolProperty(bool n)
        {
            return new BoolProperty(n);
        }

        public static implicit operator bool(BoolProperty p)
        {
            return p.Value;
        }
    }

    [DebuggerDisplay("ByteProperty | {Name} = {Value}")]
    public class ByteProperty : UProperty
    {
        byte _value;
        public byte Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public ByteProperty(byte val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.ByteProperty;
        }

        public ByteProperty(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            Value = (byte)stream.ReadByte();
            PropType = PropertyType.ByteProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteByteProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteByte(Value);
            }
        }
    }

    public class BioMask4Property : UProperty
    {
        byte _value;
        public byte Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public BioMask4Property(byte val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.BioMask4Property;
        }

        public BioMask4Property(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            Value = (byte)stream.ReadByte();
            PropType = PropertyType.BioMask4Property;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WritePropHeader(pcc, Name, PropType, 1);
            }
            stream.WriteByte(Value);
        }
    }

    [DebuggerDisplay("EnumProperty | {Name} = {Value.Name}")]
    public class EnumProperty : UProperty
    {
        public NameReference EnumType { get; }
        NameReference _value;
        public NameReference Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }
        public List<NameReference> EnumValues { get; }

        public EnumProperty(MemoryStream stream, IMEPackage pcc, NameReference enumType, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            EnumType = enumType;
            var eNameIdx = stream.ReadInt32();
            var eName = pcc.getNameEntry(eNameIdx);
            var eNameNumber = stream.ReadInt32();

            Value = new NameReference(eName, eNameNumber);
            EnumValues = UnrealObjectInfo.GetEnumValues(pcc.Game, enumType, true);
            PropType = PropertyType.ByteProperty;
        }

        public EnumProperty(NameReference value, NameReference enumType, MEGame meGame, NameReference? name = null) : base(name)
        {
            EnumType = enumType;
            NameReference enumVal = value;
            Value = enumVal;
            EnumValues = UnrealObjectInfo.GetEnumValues(meGame, enumType, true);
            PropType = PropertyType.ByteProperty;
        }

        /// <summary>
        /// Creates an enum property and sets the value to the first item in the values list.
        /// </summary>
        /// <param name="enumType">Name of enum</param>
        /// <param name="meGame">Which game this property is for</param>
        /// <param name="name">Optional name of EnumProperty</param>
        public EnumProperty(NameReference enumType, MEGame meGame, NameReference? name = null) : base(name)
        {
            EnumType = enumType;
            EnumValues = UnrealObjectInfo.GetEnumValues(meGame, enumType, true);
            if (EnumValues == null)
            {
                Debugger.Break();
            }
            Value = EnumValues[0];
            PropType = PropertyType.ByteProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteEnumProperty(pcc, Name, EnumType, Value);
            }
            else
            {
                stream.WriteInt32(pcc.FindNameOrAdd(Value.Name));
                stream.WriteInt32(Value.Number);
            }
        }
    }

    public abstract class ArrayPropertyBase : UProperty, IEnumerable
    {
        public abstract IReadOnlyList<UProperty> Properties { get; }
        public abstract int Count { get; }
        public bool IsReadOnly => true;

        protected ArrayPropertyBase(NameReference? name) : base(name)
        {
        }

        public IEnumerator GetEnumerator() => Properties.GetEnumerator();

        public abstract void Clear();

        public abstract void RemoveAt(int index);

        public UProperty this[int index] => Properties[index];

        public abstract void SwapElements(int i, int j);
    }

    [DebuggerDisplay("ArrayProperty<{typeof(T).Name,nq}> | {Name}, Length = {Values.Count}")]
    public class ArrayProperty<T> : ArrayPropertyBase, IList<T> where T : UProperty
    {
        public List<T> Values { get; set; }
        public override IReadOnlyList<UProperty> Properties => Values;

        public ArrayProperty(long startOffset, List<T> values, NameReference name) : this(values, name)
        {
            ValueOffset = startOffset;
        }

        public ArrayProperty(NameReference name) : this(new List<T>(), name)
        {
        }

        public ArrayProperty(IEnumerable<T> values, NameReference name) : this(values.ToList(), name)
        {
        }

        public ArrayProperty(List<T> values, NameReference name) : base(name)
        {
            PropType = PropertyType.ArrayProperty;
            Values = values;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteArrayProperty(pcc, Name, Values.Count, () =>
                {
                    MemoryStream m = new MemoryStream();
                    foreach (var prop in Values)
                    {
                        prop.WriteTo(m, pcc, true);
                    }
                    return m;
                });
            }
            else
            {
                stream.WriteInt32(Values.Count);
                foreach (var prop in Values)
                {
                    prop.WriteTo(stream, pcc, true);
                }
            }
        }

        #region IEnumerable<T>
        public new IEnumerator<T> GetEnumerator()
        {
            return Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Values.GetEnumerator();
        }
        #endregion

        #region IList<T>
        public override int Count => Values.Count;
        public new bool IsReadOnly => ((ICollection<T>)Values).IsReadOnly;

        public new T this[int index]
        {
            get => Values[index];
            set => Values[index] = value;
        }

        public void Add(T item)
        {
            Values.Add(item);
        }

        public override void Clear()
        {
            Values.Clear();
        }

        public bool Contains(T item)
        {
            return Values.Contains(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            Values.CopyTo(array, arrayIndex);
        }

        public bool Remove(T item)
        {
            return Values.Remove(item);
        }

        public override void RemoveAt(int index)
        {
            Values.RemoveAt(index);
        }

        public int IndexOf(T item)
        {
            return Values.IndexOf(item);
        }

        public void Insert(int index, T item)
        {
            Values.Insert(index, item);
        }

        public void InsertRange(int index, IEnumerable<T> collection)
        {
            Values.InsertRange(index, collection);
        }
        #endregion

        public override void SwapElements(int i, int j)
        {
            if (i == j || i < 0 || i >= Count || j < 0 || j >= Count)
            {
                return;
            }

            (this[i], this[j]) = (this[j], this[i]);
        }
    }

    [DebuggerDisplay("StrProperty | {Name} = {Value}")]
    public class StrProperty : UProperty
    {
        string _value;
        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public StrProperty(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            int count = stream.ReadInt32();
            var streamPos = stream.Position;

            if (count < -1) // originally 0
            {
                count *= -2;
                Value = stream.ReadStringUnicodeNull(count);
            }
            else if (count > 0)
            {
                Value = stream.ReadStringASCIINull(count);
            }
            else
            {
                Value = string.Empty;
                //ME3Explorer 3.0.2 and below wrote a null terminator character when writing an empty string.
                //The game however does not write an empty string if the length is 0 - it just happened to still work but not 100% of the time
                //This is for backwards compatibility with that as it will have a count of 0 instead of -1
                if (count == -1)
                {
                    stream.Position += 2;
                }
            }

            //for when the end of the string has multiple nulls at the end
            if (stream.Position < streamPos + count)
            {
                stream.Seek(streamPos + count, SeekOrigin.Begin);
            }

            PropType = PropertyType.StrProperty;
        }

        public StrProperty(string val, NameReference? name = null) : base(name)
        {
            Value = val ?? string.Empty;
            PropType = PropertyType.StrProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteStringProperty(pcc, Name, Value);
            }
            else
            {
                if (pcc.Game == MEGame.ME3)
                {
                    stream.WriteUnrealStringUnicode(Value);
                }
                else
                {
                    stream.WriteUnrealStringASCII(Value);
                }
            }
        }

        public static implicit operator StrProperty(string s)
        {
            return new StrProperty(s);
        }

        public static implicit operator string(StrProperty p)
        {
            return p.Value;
        }

        public override string ToString()
        {
            return Value;
        }
    }

    [DebuggerDisplay("StringRefProperty | {Name} = {Value}")]
    public class StringRefProperty : UProperty
    {
        int _value;
        public int Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public StringRefProperty(MemoryStream stream, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            Value = stream.ReadInt32();
            PropType = PropertyType.StringRefProperty;
        }

        /// <summary>
        /// For constructing new property
        /// </summary>
        /// <param name="name"></param>
        public StringRefProperty(NameReference? name = null) : base(name)
        {
            PropType = PropertyType.StringRefProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteStringRefProperty(pcc, Name, Value);
            }
            else
            {
                stream.WriteInt32(Value);
            }
        }

        public StringRefProperty(int val, NameReference? name = null) : base(name)
        {
            Value = val;
            PropType = PropertyType.StringRefProperty;
        }
    }

    public class DelegateProperty : UProperty
    {
        public int unk;
        public NameReference Value;

        public DelegateProperty(MemoryStream stream, IMEPackage pcc, NameReference? name = null) : base(name)
        {
            unk = stream.ReadInt32();
            Value = new NameReference(pcc.getNameEntry(stream.ReadInt32()), stream.ReadInt32());
            PropType = PropertyType.DelegateProperty;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteDelegateProperty(pcc, Name, unk, Value);
            }
            else
            {
                stream.WriteInt32(unk);
                stream.WriteInt32(pcc.FindNameOrAdd(Value.Name));
                stream.WriteInt32(Value.Number);
            }
        }
    }

    public class UnknownProperty : UProperty
    {
        public byte[] raw;
        public readonly string TypeName;

        public UnknownProperty(NameReference? name = null) : base(name)
        {
            raw = new byte[0];
        }

        public UnknownProperty(MemoryStream stream, int size, string typeName = null, NameReference? name = null) : base(name)
        {
            ValueOffset = stream.Position;
            TypeName = typeName ?? "Unknown";
            raw = stream.ReadToBuffer(size);
            PropType = PropertyType.Unknown;
        }

        public override void WriteTo(Stream stream, IMEPackage pcc, bool valueOnly = false)
        {
            if (!valueOnly)
            {
                stream.WriteInt32(pcc.FindNameOrAdd(Name));
                stream.WriteInt32(0);
                stream.WriteInt32(pcc.FindNameOrAdd(TypeName));
                stream.WriteInt32(0);
                stream.WriteInt32(raw.Length);
                stream.WriteInt32(0);
            }
            stream.WriteFromBuffer(raw);
        }
    }
}
