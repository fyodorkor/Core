﻿// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.ExtensibilityServices
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Xml.Linq;
    using WixToolset.Data;
    using Wix = WixToolset.Data.Serialize;
    using WixToolset.Data.Tuples;
    using WixToolset.Extensibility;
    using WixToolset.Extensibility.Services;

    internal class ParseHelper : IParseHelper
    {
        private const string LegalLongFilenameCharacters = @"[^\\\?|><:/\*""]";  // opposite of illegal above.
        private static readonly Regex LegalLongFilename = new Regex(String.Concat("^", LegalLongFilenameCharacters, @"{1,259}$"), RegexOptions.Compiled);

        private const string LegalRelativeLongFilenameCharacters = @"[^\?|><:/\*""]"; // (like legal long, but we allow '\') illegal: ? | > < : / * "
        private static readonly Regex LegalRelativeLongFilename = new Regex(String.Concat("^", LegalRelativeLongFilenameCharacters, @"{1,259}$"), RegexOptions.Compiled);

        private const string LegalWildcardLongFilenameCharacters = @"[^\\|><:/""]"; // illegal: \ | > < : / "
        private static readonly Regex LegalWildcardLongFilename = new Regex(String.Concat("^", LegalWildcardLongFilenameCharacters, @"{1,259}$"));

        private static readonly Regex LegalIdentifierWithAccess = new Regex(@"^((?<access>public|internal|protected|private)\s+)?(?<id>[_A-Za-z][0-9A-Za-z_\.]*)$", RegexOptions.Compiled | RegexOptions.ExplicitCapture);

        private static readonly Regex PutGuidHere = new Regex(@"PUT\-GUID\-(?:\d+\-)?HERE", RegexOptions.Singleline);

        public ParseHelper(IServiceProvider serviceProvider)
        {
            this.ServiceProvider = serviceProvider;

            this.Messaging = serviceProvider.GetService<IMessaging>();
        }

        private IServiceProvider ServiceProvider { get; }

        private IMessaging Messaging { get; }

        private ITupleDefinitionCreator Creator { get; set; }

        public bool ContainsProperty(string possibleProperty)
        {
            return Common.ContainsProperty(possibleProperty);
        }

        public void CreateComplexReference(IntermediateSection section, SourceLineNumber sourceLineNumbers, ComplexReferenceParentType parentType, string parentId, string parentLanguage, ComplexReferenceChildType childType, string childId, bool isPrimary)
        {
            var wixComplexReferenceRow = (WixComplexReferenceTuple)this.CreateRow(section, sourceLineNumbers, TupleDefinitionType.WixComplexReference);
            wixComplexReferenceRow.Parent = parentId;
            wixComplexReferenceRow.ParentType = parentType;
            wixComplexReferenceRow.ParentLanguage = parentLanguage;
            wixComplexReferenceRow.Child = childId;
            wixComplexReferenceRow.ChildType = childType;
            wixComplexReferenceRow.IsPrimary = isPrimary;

            this.CreateWixGroupRow(section, sourceLineNumbers, parentType, parentId, childType, childId);
        }

        public Identifier CreateDirectoryRow(IntermediateSection section, SourceLineNumber sourceLineNumbers, Identifier id, string parentId, string name, string shortName = null, string sourceName = null, string shortSourceName = null, ISet<string> sectionInlinedDirectoryIds = null)
        {
            string defaultDir = null;

            if (name.Equals("SourceDir") || this.IsValidShortFilename(name, false))
            {
                defaultDir = name;
            }
            else
            {
                if (String.IsNullOrEmpty(shortName))
                {
                    shortName = this.CreateShortName(name, false, false, "Directory", parentId);
                }

                defaultDir = String.Concat(shortName, "|", name);
            }

            if (!String.IsNullOrEmpty(sourceName))
            {
                if (this.IsValidShortFilename(sourceName, false))
                {
                    defaultDir = String.Concat(defaultDir, ":", sourceName);
                }
                else
                {
                    if (String.IsNullOrEmpty(shortSourceName))
                    {
                        shortSourceName = this.CreateShortName(sourceName, false, false, "Directory", parentId);
                    }

                    defaultDir = String.Concat(defaultDir, ":", shortSourceName, "|", sourceName);
                }
            }

            // For anonymous directories, create the identifier. If this identifier already exists in the
            // active section, bail so we don't add duplicate anonymous directory rows (which are legal
            // but bloat the intermediate and ultimately make the linker do "busy work").
            if (null == id)
            {
                id = this.CreateIdentifier("dir", parentId, name, shortName, sourceName, shortSourceName);

                if (!sectionInlinedDirectoryIds.Add(id.Id))
                {
                    return id;
                }
            }

            var row = this.CreateRow(section, sourceLineNumbers, TupleDefinitionType.Directory, id);
            row.Set(1, parentId);
            row.Set(2, defaultDir);
            return id;
        }

        public string CreateDirectoryReferenceFromInlineSyntax(IntermediateSection section, SourceLineNumber sourceLineNumbers, XAttribute attribute, string parentId)
        {
            string id = null;
            string[] inlineSyntax = this.GetAttributeInlineDirectorySyntax(sourceLineNumbers, attribute, true);

            if (null != inlineSyntax)
            {
                // Special case the single entry in the inline syntax since it is the most common case
                // and needs no extra processing. It's just a reference to an existing directory.
                if (1 == inlineSyntax.Length)
                {
                    id = inlineSyntax[0];
                    this.CreateSimpleReference(section, sourceLineNumbers, "Directory", id);
                }
                else // start creating rows for the entries in the inline syntax
                {
                    id = parentId;

                    int pathStartsAt = 0;
                    if (inlineSyntax[0].EndsWith(":"))
                    {
                        // TODO: should overriding the parent identifier with a specific id be an error or a warning or just let it slide?
                        //if (null != parentId)
                        //{
                        //    this.core.Write(WixErrors.Xxx(sourceLineNumbers));
                        //}

                        id = inlineSyntax[0].TrimEnd(':');
                        this.CreateSimpleReference(section, sourceLineNumbers, "Directory", id);

                        pathStartsAt = 1;
                    }

                    for (int i = pathStartsAt; i < inlineSyntax.Length; ++i)
                    {
                        Identifier inlineId = this.CreateDirectoryRow(section, sourceLineNumbers, null, id, inlineSyntax[i]);
                        id = inlineId.Id;
                    }
                }
            }

            return id;
        }

        public string CreateGuid(Guid namespaceGuid, string value)
        {
            return Uuid.NewUuid(namespaceGuid, value).ToString("B").ToUpperInvariant();
        }

        public Identifier CreateIdentifier(string prefix, params string[] args)
        {
            var id = Common.GenerateIdentifier(prefix, args);
            return new Identifier(id, AccessModifier.Private);
        }

        public Identifier CreateIdentifierFromFilename(string filename)
        {
            var id = Common.GetIdentifierFromName(filename);
            return new Identifier(id, AccessModifier.Private);
        }

        public Identifier CreateRegistryRow(IntermediateSection section, SourceLineNumber sourceLineNumbers, int root, string key, string name, string value, string componentId, bool escapeLeadingHash)
        {
            Identifier id = null;

            if (-1 > root || 3 < root)
            {
                throw new ArgumentOutOfRangeException("root");
            }

            if (null == key)
            {
                throw new ArgumentNullException("key");
            }

            if (null == componentId)
            {
                throw new ArgumentNullException("componentId");
            }

            // Escape the leading '#' character for string registry values.
            if (escapeLeadingHash && null != value && value.StartsWith("#", StringComparison.Ordinal))
            {
                value = String.Concat("#", value);
            }

            id = this.CreateIdentifier("reg", componentId, root.ToString(CultureInfo.InvariantCulture.NumberFormat), key.ToLowerInvariant(), (null != name ? name.ToLowerInvariant() : name));

            var row = this.CreateRow(section, sourceLineNumbers, TupleDefinitionType.Registry, id);
            row.Set(1, root);
            row.Set(2, key);
            row.Set(3, name);
            row.Set(4, value);
            row.Set(5, componentId);

            return id;
        }

        public void CreateSimpleReference(IntermediateSection section, SourceLineNumber sourceLineNumbers, string tableName, params string[] primaryKeys)
        {
            var joinedKeys = String.Join("/", primaryKeys);
            var id = String.Concat(tableName, ":", joinedKeys);

            var wixSimpleReferenceRow = (WixSimpleReferenceTuple)this.CreateRow(section, sourceLineNumbers, TupleDefinitionType.WixSimpleReference);
            wixSimpleReferenceRow.Table = tableName;
            wixSimpleReferenceRow.PrimaryKeys = joinedKeys;
        }

        public void CreateWixGroupRow(IntermediateSection section, SourceLineNumber sourceLineNumbers, ComplexReferenceParentType parentType, string parentId, ComplexReferenceChildType childType, string childId)
        {
            if (null == parentId || ComplexReferenceParentType.Unknown == parentType)
            {
                return;
            }

            if (null == childId)
            {
                throw new ArgumentNullException("childId");
            }

            var row = (WixGroupTuple)this.CreateRow(section, sourceLineNumbers, TupleDefinitionType.WixGroup);
            row.ParentId = parentId;
            row.ParentType = parentType;
            row.ChildId = childId;
            row.ChildType = childType;
        }

        public IntermediateTuple CreateRow(IntermediateSection section, SourceLineNumber sourceLineNumbers, string tableName, Identifier identifier = null)
        {
            if (this.Creator == null)
            {
                this.CreateTupleDefinitionCreator();
            }

            if (!this.Creator.TryGetTupleDefinitionByName(tableName, out var tupleDefinition))
            {
                throw new ArgumentException(nameof(tableName));
            }

            return CreateRow(section, sourceLineNumbers, tupleDefinition, identifier);
        }

        public IntermediateTuple CreateRow(IntermediateSection section, SourceLineNumber sourceLineNumbers, TupleDefinitionType tupleType, Identifier identifier = null)
        {
            var tupleDefinition = TupleDefinitions.ByType(tupleType);

            return CreateRow(section, sourceLineNumbers, tupleDefinition, identifier);
        }

        public string CreateShortName(string longName, bool keepExtension, bool allowWildcards, params string[] args)
        {
            // canonicalize the long name if its not a localization identifier (they are case-sensitive)
            if (!this.IsValidLocIdentifier(longName))
            {
                longName = longName.ToLowerInvariant();
            }

            // collect all the data
            List<string> strings = new List<string>(1 + args.Length);
            strings.Add(longName);
            strings.AddRange(args);

            // prepare for hashing
            string stringData = String.Join("|", strings);
            byte[] data = Encoding.UTF8.GetBytes(stringData);

            // hash the data
            byte[] hash;
            using (var sha1 = new SHA1CryptoServiceProvider())
            {
                hash = sha1.ComputeHash(data);
            }

            // generate the short file/directory name without an extension
            StringBuilder shortName = new StringBuilder(Convert.ToBase64String(hash));
            shortName.Remove(8, shortName.Length - 8).Replace('+', '-').Replace('/', '_');

            if (keepExtension)
            {
                string extension = Path.GetExtension(longName);

                if (4 < extension.Length)
                {
                    extension = extension.Substring(0, 4);
                }

                shortName.Append(extension);

                // check the generated short name to ensure its still legal (the extension may not be legal)
                if (!this.IsValidShortFilename(shortName.ToString(), allowWildcards))
                {
                    // remove the extension (by truncating the generated file name back to the generated characters)
                    shortName.Length -= extension.Length;
                }
            }

            return shortName.ToString().ToLowerInvariant();
        }

        public void EnsureTable(IntermediateSection section, SourceLineNumber sourceLineNumbers, string tableName)
        {
            var row = this.CreateRow(section, sourceLineNumbers, TupleDefinitionType.WixEnsureTable);
            row.Set(0, tableName);

            if (this.Creator == null)
            {
                this.CreateTupleDefinitionCreator();
            }

            // We don't add custom table definitions to the tableDefinitions collection,
            // so if it's not in there, it better be a custom table. If the Id is just wrong,
            // instead of a custom table, we get an unresolved reference at link time.
            if (!this.Creator.TryGetTupleDefinitionByName(tableName, out var ignored))
            {
                this.CreateSimpleReference(section, sourceLineNumbers, "WixCustomTable", tableName);
            }
        }

        public string GetAttributeGuidValue(SourceLineNumber sourceLineNumbers, XAttribute attribute, bool generatable = false, bool canBeEmpty = false)
        {
            if (null == attribute)
            {
                throw new ArgumentNullException("attribute");
            }

            EmptyRule emptyRule = canBeEmpty ? EmptyRule.CanBeEmpty : EmptyRule.CanBeWhitespaceOnly;
            string value = this.GetAttributeValue(sourceLineNumbers, attribute, emptyRule);

            if (String.IsNullOrEmpty(value) && canBeEmpty)
            {
                return String.Empty;
            }
            else if (!String.IsNullOrEmpty(value))
            {
                // If the value starts and ends with braces or parenthesis, accept that and strip them off.
                if ((value.StartsWith("{", StringComparison.Ordinal) && value.EndsWith("}", StringComparison.Ordinal))
                    || (value.StartsWith("(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal)))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                if (generatable && "*".Equals(value, StringComparison.Ordinal))
                {
                    return value;
                }

                if (ParseHelper.PutGuidHere.IsMatch(value))
                {
                    this.Messaging.Write(ErrorMessages.ExampleGuid(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    return CompilerConstants.IllegalGuid;
                }
                else if (value.StartsWith("!(loc", StringComparison.Ordinal) || value.StartsWith("$(loc", StringComparison.Ordinal) || value.StartsWith("!(wix", StringComparison.Ordinal))
                {
                    return value;
                }
                else if (Guid.TryParse(value, out var guid))
                {
                    var uppercaseGuid = guid.ToString().ToUpperInvariant();

                    // TODO: This used to be a pedantic error, what should it be now?
                    //if (uppercaseGuid != value)
                    //{
                    //    this.Messaging.Write(WixErrors.GuidContainsLowercaseLetters(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    //}

                    return String.Concat("{", uppercaseGuid, "}");
                }
                else
                {
                    this.Messaging.Write(ErrorMessages.IllegalGuidValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                }
            }

            return CompilerConstants.IllegalGuid;
        }

        public Identifier GetAttributeIdentifier(SourceLineNumber sourceLineNumbers, XAttribute attribute)
        {
            var access = AccessModifier.Public;
            var value = Common.GetAttributeValue(this.Messaging, sourceLineNumbers, attribute, EmptyRule.CanBeEmpty);

            var match = ParseHelper.LegalIdentifierWithAccess.Match(value);
            if (!match.Success)
            {
                return null;
            }
            else if (match.Groups["access"].Success)
            {
                access = (AccessModifier)Enum.Parse(typeof(AccessModifier), match.Groups["access"].Value, true);
            }

            value = match.Groups["id"].Value;

            if (Common.IsIdentifier(value) && 72 < value.Length)
            {
                this.Messaging.Write(WarningMessages.IdentifierTooLong(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
            }

            return new Identifier(value, access);
        }

        public string GetAttributeIdentifierValue(SourceLineNumber sourceLineNumbers, XAttribute attribute)
        {
            return Common.GetAttributeIdentifierValue(this.Messaging, sourceLineNumbers, attribute);
        }

        public string[] GetAttributeInlineDirectorySyntax(SourceLineNumber sourceLineNumbers, XAttribute attribute, bool resultUsedToCreateReference = false)
        {
            string[] result = null;
            string value = this.GetAttributeValue(sourceLineNumbers, attribute);

            if (!String.IsNullOrEmpty(value))
            {
                int pathStartsAt = 0;
                result = value.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (result[0].EndsWith(":", StringComparison.Ordinal))
                {
                    string id = result[0].TrimEnd(':');
                    if (1 == result.Length)
                    {
                        this.Messaging.Write(ErrorMessages.InlineDirectorySyntaxRequiresPath(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value, id));
                        return null;
                    }
                    else if (!this.IsValidIdentifier(id))
                    {
                        this.Messaging.Write(ErrorMessages.IllegalIdentifier(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value, id));
                        return null;
                    }

                    pathStartsAt = 1;
                }
                else if (resultUsedToCreateReference && 1 == result.Length)
                {
                    if (value.EndsWith("\\"))
                    {
                        if (!this.IsValidLongFilename(result[0], false, false))
                        {
                            this.Messaging.Write(ErrorMessages.IllegalLongFilename(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value, result[0]));
                            return null;
                        }
                    }
                    else if (!this.IsValidIdentifier(result[0]))
                    {
                        this.Messaging.Write(ErrorMessages.IllegalIdentifier(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value, result[0]));
                        return null;
                    }

                    return result; // return early to avoid additional checks below.
                }

                // Check each part of the relative path to ensure that it is a valid directory name.
                for (int i = pathStartsAt; i < result.Length; ++i)
                {
                    if (!this.IsValidLongFilename(result[i], false, false))
                    {
                        this.Messaging.Write(ErrorMessages.IllegalLongFilename(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value, result[i]));
                        return null;
                    }
                }

                if (1 < result.Length && !value.EndsWith("\\"))
                {
                    this.Messaging.Write(WarningMessages.BackslashTerminateInlineDirectorySyntax(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                }
            }

            return result;
        }

        public int GetAttributeIntegerValue(SourceLineNumber sourceLineNumbers, XAttribute attribute, int minimum, int maximum)
        {
            return Common.GetAttributeIntegerValue(this.Messaging, sourceLineNumbers, attribute, minimum, maximum);
        }

        public string GetAttributeLongFilename(SourceLineNumber sourceLineNumbers, XAttribute attribute, bool allowWildcards, bool allowRelative)
        {
            if (null == attribute)
            {
                throw new ArgumentNullException("attribute");
            }

            string value = this.GetAttributeValue(sourceLineNumbers, attribute);

            if (0 < value.Length)
            {
                if (!this.IsValidLongFilename(value, allowWildcards, allowRelative) && !this.IsValidLocIdentifier(value))
                {
                    if (allowRelative)
                    {
                        this.Messaging.Write(ErrorMessages.IllegalRelativeLongFilename(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    }
                    else
                    {
                        this.Messaging.Write(ErrorMessages.IllegalLongFilename(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    }
                }
                else if (allowRelative)
                {
                    string normalizedPath = value.Replace('\\', '/');
                    if (normalizedPath.StartsWith("../", StringComparison.Ordinal) || normalizedPath.Contains("/../"))
                    {
                        this.Messaging.Write(ErrorMessages.PayloadMustBeRelativeToCache(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    }
                }
                else if (CompilerCore.IsAmbiguousFilename(value))
                {
                    this.Messaging.Write(WarningMessages.AmbiguousFileOrDirectoryName(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                }
            }

            return value;
        }

        public long GetAttributeLongValue(SourceLineNumber sourceLineNumbers, XAttribute attribute, long minimum, long maximum)
        {
            Debug.Assert(minimum > CompilerConstants.LongNotSet && minimum > CompilerConstants.IllegalLong, "The legal values for this attribute collide with at least one sentinel used during parsing.");

            string value = this.GetAttributeValue(sourceLineNumbers, attribute);

            if (0 < value.Length)
            {
                try
                {
                    long longValue = Convert.ToInt64(value, CultureInfo.InvariantCulture.NumberFormat);

                    if (CompilerConstants.LongNotSet == longValue || CompilerConstants.IllegalLong == longValue)
                    {
                        this.Messaging.Write(ErrorMessages.IntegralValueSentinelCollision(sourceLineNumbers, longValue));
                    }
                    else if (minimum > longValue || maximum < longValue)
                    {
                        this.Messaging.Write(ErrorMessages.IntegralValueOutOfRange(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, longValue, minimum, maximum));
                        longValue = CompilerConstants.IllegalLong;
                    }

                    return longValue;
                }
                catch (FormatException)
                {
                    this.Messaging.Write(ErrorMessages.IllegalLongValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                }
                catch (OverflowException)
                {
                    this.Messaging.Write(ErrorMessages.IllegalLongValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                }
            }

            return CompilerConstants.IllegalLong;
        }

        public string GetAttributeValue(SourceLineNumber sourceLineNumbers, XAttribute attribute, EmptyRule emptyRule = EmptyRule.CanBeWhitespaceOnly)
        {
            return Common.GetAttributeValue(this.Messaging, sourceLineNumbers, attribute, emptyRule);
        }

        public int GetAttributeMsidbRegistryRootValue(SourceLineNumber sourceLineNumbers, XAttribute attribute, bool allowHkmu)
        {
            Wix.RegistryRootType registryRoot = this.GetAttributeRegistryRootValue(sourceLineNumbers, attribute, allowHkmu);

            switch (registryRoot)
            {
                case Wix.RegistryRootType.NotSet:
                    return CompilerConstants.IntegerNotSet;
                case Wix.RegistryRootType.HKCR:
                    return Core.Native.MsiInterop.MsidbRegistryRootClassesRoot;
                case Wix.RegistryRootType.HKCU:
                    return Core.Native.MsiInterop.MsidbRegistryRootCurrentUser;
                case Wix.RegistryRootType.HKLM:
                    return Core.Native.MsiInterop.MsidbRegistryRootLocalMachine;
                case Wix.RegistryRootType.HKU:
                    return Core.Native.MsiInterop.MsidbRegistryRootUsers;
                case Wix.RegistryRootType.HKMU:
                    // This is gross, but there was *one* registry root parsing instance
                    // (in Compiler.ParseRegistrySearchElement()) that did not explicitly
                    // handle HKMU and it fell through to the default error case. The
                    // others treated it as -1, which is what we do here.
                    if (allowHkmu)
                    {
                        return -1;
                    }
                    break;
            }

            return CompilerConstants.IntegerNotSet;
        }

        public string GetAttributeVersionValue(SourceLineNumber sourceLineNumbers, XAttribute attribute)
        {
            var value = this.GetAttributeValue(sourceLineNumbers, attribute);

            if (!String.IsNullOrEmpty(value))
            {
                if (Version.TryParse(value, out var version))
                {
                    return version.ToString();
                }

                // Allow versions to contain binder variables.
                if (Common.ContainsValidBinderVariable(value))
                {
                    return value;
                }

                this.Messaging.Write(ErrorMessages.IllegalVersionValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
            }

            return null;
        }

        public YesNoDefaultType GetAttributeYesNoDefaultValue(SourceLineNumber sourceLineNumbers, XAttribute attribute)
        {
            var value = this.GetAttributeValue(sourceLineNumbers, attribute);

            switch (value)
            {
                case "yes":
                case "true":
                    return YesNoDefaultType.Yes;

                case "no":
                case "false":
                    return YesNoDefaultType.No;

                case "default":
                    return YesNoDefaultType.Default;

                default:
                    this.Messaging.Write(ErrorMessages.IllegalYesNoDefaultValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    return YesNoDefaultType.IllegalValue;
            }
        }

        public YesNoType GetAttributeYesNoValue(SourceLineNumber sourceLineNumbers, XAttribute attribute)
        {
            var value = this.GetAttributeValue(sourceLineNumbers, attribute);

            switch (value)
            {
                case "yes":
                case "true":
                    return YesNoType.Yes;

                case "no":
                case "false":
                    return YesNoType.No;

                default:
                    this.Messaging.Write(ErrorMessages.IllegalYesNoValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value));
                    return YesNoType.IllegalValue;
            }
        }

        public SourceLineNumber GetSourceLineNumbers(XElement element)
        {
            return Preprocessor.GetSourceLineNumbers(element);
        }

        public string GetConditionInnerText(XElement element)
        {
            var value = Common.GetInnerText(element)?.Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

            // Return null for a non-existant condition.
            return String.IsNullOrEmpty(value) ? null : value;
        }

        public string GetTrimmedInnerText(XElement element)
        {
            var value = Common.GetInnerText(element);
            return value?.Trim();
        }

        public bool IsValidIdentifier(string value)
        {
            return Common.IsIdentifier(value);
        }

        public bool IsValidLocIdentifier(string identifier)
        {
            if (String.IsNullOrEmpty(identifier))
            {
                return false;
            }

            var match = Common.WixVariableRegex.Match(identifier);

            return (match.Success && "loc" == match.Groups["namespace"].Value && 0 == match.Index && identifier.Length == match.Length);
        }

        public bool IsValidLongFilename(string filename, bool allowWildcards, bool allowRelative)
        {
            if (String.IsNullOrEmpty(filename))
            {
                return false;
            }

            // Check for a non-period character (all periods is not legal)
            bool nonPeriodFound = false;
            foreach (char character in filename)
            {
                if ('.' != character)
                {
                    nonPeriodFound = true;
                    break;
                }
            }

            if (allowWildcards)
            {
                return (nonPeriodFound && ParseHelper.LegalWildcardLongFilename.IsMatch(filename));
            }
            else if (allowRelative)
            {
                return (nonPeriodFound && ParseHelper.LegalRelativeLongFilename.IsMatch(filename));
            }
            else
            {
                return (nonPeriodFound && ParseHelper.LegalLongFilename.IsMatch(filename));
            }
        }

        public bool IsValidShortFilename(string filename, bool allowWildcards = false)
        {
            return Common.IsValidShortFilename(filename, allowWildcards);
        }

        public void ParseExtensionAttribute(IEnumerable<ICompilerExtension> extensions, Intermediate intermediate, IntermediateSection section, XElement element, XAttribute attribute, IDictionary<string, string> context = null)
        {
            // Ignore attributes defined by the W3C because we'll assume they are always right.
            if ((String.IsNullOrEmpty(attribute.Name.NamespaceName) && attribute.Name.LocalName.Equals("xmlns", StringComparison.Ordinal)) ||
                attribute.Name.NamespaceName.StartsWith(CompilerCore.W3SchemaPrefix.NamespaceName, StringComparison.Ordinal))
            {
                return;
            }

            if (ParseHelper.TryFindExtension(extensions, attribute.Name.NamespaceName, out var extension))
            {
                extension.ParseAttribute(intermediate, section, element, attribute, context);
            }
            else
            {
                var sourceLineNumbers = Preprocessor.GetSourceLineNumbers(element);
                this.Messaging.Write(ErrorMessages.UnhandledExtensionAttribute(sourceLineNumbers, element.Name.LocalName, attribute.Name.LocalName, attribute.Name.NamespaceName));
            }
        }

        public void ParseExtensionElement(IEnumerable<ICompilerExtension> extensions, Intermediate intermediate, IntermediateSection section, XElement parentElement, XElement element, IDictionary<string, string> context = null)
        {
            if (ParseHelper.TryFindExtension(extensions, element.Name.Namespace, out var extension))
            {
                SourceLineNumber sourceLineNumbers = Preprocessor.GetSourceLineNumbers(parentElement);
                extension.ParseElement(intermediate, section, parentElement, element, context);
            }
            else
            {
                var childSourceLineNumbers = Preprocessor.GetSourceLineNumbers(element);
                this.Messaging.Write(ErrorMessages.UnhandledExtensionElement(childSourceLineNumbers, parentElement.Name.LocalName, element.Name.LocalName, element.Name.NamespaceName));
            }
        }

        public ComponentKeyPath ParsePossibleKeyPathExtensionElement(IEnumerable<ICompilerExtension> extensions, Intermediate intermediate, IntermediateSection section, XElement parentElement, XElement element, IDictionary<string, string> context)
        {
            ComponentKeyPath keyPath = null;

            if (ParseHelper.TryFindExtension(extensions, element.Name.Namespace, out var extension))
            {
                keyPath = extension.ParsePossibleKeyPathElement(intermediate, section, parentElement, element, context);
            }
            else
            {
                var childSourceLineNumbers = Preprocessor.GetSourceLineNumbers(element);
                this.Messaging.Write(ErrorMessages.UnhandledExtensionElement(childSourceLineNumbers, parentElement.Name.LocalName, element.Name.LocalName, element.Name.NamespaceName));
            }

            return keyPath;
        }

        public void ParseForExtensionElements(IEnumerable<ICompilerExtension> extensions, Intermediate intermediate, IntermediateSection section, XElement element)
        {
            foreach (XElement child in element.Elements())
            {
                if (element.Name.Namespace == child.Name.Namespace)
                {
                    this.UnexpectedElement(element, child);
                }
                else
                {
                    this.ParseExtensionElement(extensions, intermediate, section, element, child);
                }
            }
        }

        public void UnexpectedAttribute(XElement element, XAttribute attribute)
        {
            var sourceLineNumbers = Preprocessor.GetSourceLineNumbers(element);
            Common.UnexpectedAttribute(this.Messaging, sourceLineNumbers, attribute);
        }

        public void UnexpectedElement(XElement parentElement, XElement childElement)
        {
            var sourceLineNumbers = Preprocessor.GetSourceLineNumbers(childElement);
            this.Messaging.Write(ErrorMessages.UnexpectedElement(sourceLineNumbers, parentElement.Name.LocalName, childElement.Name.LocalName));
        }

        private void CreateTupleDefinitionCreator()
        {
            this.Creator = (ITupleDefinitionCreator)this.ServiceProvider.GetService(typeof(ITupleDefinitionCreator));
        }

        private static IntermediateTuple CreateRow(IntermediateSection section, SourceLineNumber sourceLineNumbers, IntermediateTupleDefinition tupleDefinition, Identifier identifier)
        {
            var row = tupleDefinition.CreateTuple(sourceLineNumbers, identifier);

            if (null != identifier)
            {
                if (row.Definition.FieldDefinitions[0].Type == IntermediateFieldType.Number)
                {
                    row.Set(0, Convert.ToInt32(identifier.Id));
                }
                else
                {
                    row.Set(0, identifier.Id);
                }
            }

            section.Tuples.Add(row);

            return row;
        }

        private Wix.RegistryRootType GetAttributeRegistryRootValue(SourceLineNumber sourceLineNumbers, XAttribute attribute, bool allowHkmu)
        {
            Wix.RegistryRootType registryRoot = Wix.RegistryRootType.NotSet;
            string value = this.GetAttributeValue(sourceLineNumbers, attribute);

            if (0 < value.Length)
            {
                registryRoot = Wix.Enums.ParseRegistryRootType(value);

                if (Wix.RegistryRootType.IllegalValue == registryRoot || (!allowHkmu && Wix.RegistryRootType.HKMU == registryRoot))
                {
                    // TODO: Find a way to expose the valid values programatically!
                    if (allowHkmu)
                    {
                        this.Messaging.Write(ErrorMessages.IllegalAttributeValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value,
                            "HKMU", "HKCR", "HKCU", "HKLM", "HKU"));
                    }
                    else
                    {
                        this.Messaging.Write(ErrorMessages.IllegalAttributeValue(sourceLineNumbers, attribute.Parent.Name.LocalName, attribute.Name.LocalName, value,
                            "HKCR", "HKCU", "HKLM", "HKU"));
                    }
                }
            }

            return registryRoot;
        }

        private static bool TryFindExtension(IEnumerable<ICompilerExtension> extensions, XNamespace ns, out ICompilerExtension extension)
        {
            extension = null;

            foreach (var ext in extensions)
            {
                if (ext.Namespace == ns)
                {
                    extension = ext;
                    break;
                }
            }

            return extension != null;
        }
    }
}
