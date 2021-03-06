// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core
{
    using System;
    using System.Collections.Generic;
    using System.Xml.Linq;
    using WixToolset.Core.Native;
    using WixToolset.Data;
    using WixToolset.Data.Bind;
    using WixToolset.Extensibility;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// Parses localization files and localizes database values.
    /// </summary>
    public sealed class Localizer
    {
        public static readonly XNamespace WxlNamespace = "http://wixtoolset.org/schemas/v4/wxl";
        private static string XmlElementName = "WixLocalization";

        /// <summary>
        /// Loads a localization file from a path on disk.
        /// </summary>
        /// <param name="path">Path to localization file saved on disk.</param>
        /// <returns>Returns the loaded localization file.</returns>
        public static Localization ParseLocalizationFile(IMessaging messaging, string path)
        {
            var document = XDocument.Load(path);
            return ParseLocalizationFile(messaging, document);
        }

        /// <summary>
        /// Loads a localization file from memory.
        /// </summary>
        /// <param name="document">Document to parse as localization file.</param>
        /// <returns>Returns the loaded localization file.</returns>
        public static Localization ParseLocalizationFile(IMessaging messaging, XDocument document)
        {
            XElement root = document.Root;
            Localization localization = null;

            SourceLineNumber sourceLineNumbers = SourceLineNumber.CreateFromXObject(root);
            if (Localizer.XmlElementName == root.Name.LocalName)
            {
                if (Localizer.WxlNamespace == root.Name.Namespace)
                {
                    localization = ParseWixLocalizationElement(messaging, root);
                }
                else // invalid or missing namespace
                {
                    if (null == root.Name.Namespace)
                    {
                        messaging.Write(ErrorMessages.InvalidWixXmlNamespace(sourceLineNumbers, Localizer.XmlElementName, Localizer.WxlNamespace.NamespaceName));
                    }
                    else
                    {
                        messaging.Write(ErrorMessages.InvalidWixXmlNamespace(sourceLineNumbers, Localizer.XmlElementName, root.Name.LocalName, Localizer.WxlNamespace.NamespaceName));
                    }
                }
            }
            else
            {
                messaging.Write(ErrorMessages.InvalidDocumentElement(sourceLineNumbers, root.Name.LocalName, "localization", Localizer.XmlElementName));
            }

            return localization;
        }

        /// <summary>
        /// Adds a WixVariableRow to a dictionary while performing the expected override checks.
        /// </summary>
        /// <param name="variables">Dictionary of variable rows.</param>
        /// <param name="wixVariableRow">Row to add to the variables dictionary.</param>
        private static void AddWixVariable(IMessaging messaging, IDictionary<string, BindVariable> variables, BindVariable wixVariableRow)
        {
            if (!variables.TryGetValue(wixVariableRow.Id, out var existingWixVariableRow) || (existingWixVariableRow.Overridable && !wixVariableRow.Overridable))
            {
                variables[wixVariableRow.Id] = wixVariableRow;
            }
            else if (!wixVariableRow.Overridable)
            {
                messaging.Write(ErrorMessages.DuplicateLocalizationIdentifier(wixVariableRow.SourceLineNumbers, wixVariableRow.Id));
            }
        }

        /// <summary>
        /// Parses the WixLocalization element.
        /// </summary>
        /// <param name="node">Element to parse.</param>
        private static Localization ParseWixLocalizationElement(IMessaging messaging, XElement node)
        {
            int codepage = -1;
            string culture = null;
            SourceLineNumber sourceLineNumbers = SourceLineNumber.CreateFromXObject(node);

            foreach (XAttribute attrib in node.Attributes())
            {
                if (String.IsNullOrEmpty(attrib.Name.NamespaceName) || Localizer.WxlNamespace == attrib.Name.Namespace)
                {
                    switch (attrib.Name.LocalName)
                    {
                        case "Codepage":
                            codepage = Common.GetValidCodePage(attrib.Value, true, false, sourceLineNumbers);
                            break;
                        case "Culture":
                            culture = attrib.Value;
                            break;
                        case "Language":
                            // do nothing; @Language is used for locutil which can't convert Culture to lcid
                            break;
                        default:
                            Common.UnexpectedAttribute(messaging, sourceLineNumbers, attrib);
                            break;
                    }
                }
                else
                {
                    Common.UnexpectedAttribute(messaging, sourceLineNumbers, attrib);
                }
            }

            Dictionary<string, BindVariable> variables = new Dictionary<string, BindVariable>();
            Dictionary<string, LocalizedControl> localizedControls = new Dictionary<string, LocalizedControl>();

            foreach (XElement child in node.Elements())
            {
                if (Localizer.WxlNamespace == child.Name.Namespace)
                {
                    switch (child.Name.LocalName)
                    {
                        case "String":
                            Localizer.ParseString(messaging, child, variables);
                            break;

                        case "UI":
                            Localizer.ParseUI(messaging, child, localizedControls);
                            break;

                        default:
                            messaging.Write(ErrorMessages.UnexpectedElement(sourceLineNumbers, node.Name.ToString(), child.Name.ToString()));
                            break;
                    }
                }
                else
                {
                    messaging.Write(ErrorMessages.UnsupportedExtensionElement(sourceLineNumbers, node.Name.ToString(), child.Name.ToString()));
                }
            }

            return messaging.EncounteredError ? null : new Localization(codepage, culture, variables, localizedControls);
        }

        /// <summary>
        /// Parse a localization string into a WixVariableRow.
        /// </summary>
        /// <param name="node">Element to parse.</param>
        private static void ParseString(IMessaging messaging, XElement node, IDictionary<string, BindVariable> variables)
        {
            string id = null;
            bool overridable = false;
            SourceLineNumber sourceLineNumbers = SourceLineNumber.CreateFromXObject(node);

            foreach (XAttribute attrib in node.Attributes())
            {
                if (String.IsNullOrEmpty(attrib.Name.NamespaceName) || Localizer.WxlNamespace == attrib.Name.Namespace)
                {
                    switch (attrib.Name.LocalName)
                    {
                        case "Id":
                            id = Common.GetAttributeIdentifierValue(messaging, sourceLineNumbers, attrib);
                            break;
                        case "Overridable":
                            overridable = YesNoType.Yes == Common.GetAttributeYesNoValue(messaging, sourceLineNumbers, attrib);
                            break;
                        case "Localizable":
                            ; // do nothing
                            break;
                        default:
                            messaging.Write(ErrorMessages.UnexpectedAttribute(sourceLineNumbers, attrib.Parent.Name.ToString(), attrib.Name.ToString()));
                            break;
                    }
                }
                else
                {
                    messaging.Write(ErrorMessages.UnsupportedExtensionAttribute(sourceLineNumbers, attrib.Parent.Name.ToString(), attrib.Name.ToString()));
                }
            }

            string value = Common.GetInnerText(node);

            if (null == id)
            {
                messaging.Write(ErrorMessages.ExpectedAttribute(sourceLineNumbers, "String", "Id"));
            }
            else if (0 == id.Length)
            {
                messaging.Write(ErrorMessages.IllegalIdentifier(sourceLineNumbers, "String", "Id", 0));
            }

            if (!messaging.EncounteredError)
            {
                var variable = new BindVariable
                {
                    SourceLineNumbers = sourceLineNumbers,
                    Id = id,
                    Overridable = overridable,
                    Value = value,
                };

                Localizer.AddWixVariable(messaging, variables, variable);
            }
        }

        /// <summary>
        /// Parse a localized control.
        /// </summary>
        /// <param name="node">Element to parse.</param>
        /// <param name="localizedControls">Dictionary of localized controls.</param>
        private static void ParseUI(IMessaging messaging, XElement node, IDictionary<string, LocalizedControl> localizedControls)
        {
            string dialog = null;
            string control = null;
            int x = CompilerConstants.IntegerNotSet;
            int y = CompilerConstants.IntegerNotSet;
            int width = CompilerConstants.IntegerNotSet;
            int height = CompilerConstants.IntegerNotSet;
            int attribs = 0;
            string text = null;
            SourceLineNumber sourceLineNumbers = SourceLineNumber.CreateFromXObject(node);

            foreach (XAttribute attrib in node.Attributes())
            {
                if (String.IsNullOrEmpty(attrib.Name.NamespaceName) || Localizer.WxlNamespace == attrib.Name.Namespace)
                {
                    switch (attrib.Name.LocalName)
                    {
                        case "Dialog":
                            dialog = Common.GetAttributeIdentifierValue(messaging, sourceLineNumbers, attrib);
                            break;
                        case "Control":
                            control = Common.GetAttributeIdentifierValue(messaging, sourceLineNumbers, attrib);
                            break;
                        case "X":
                            x = Common.GetAttributeIntegerValue(messaging, sourceLineNumbers, attrib, 0, short.MaxValue);
                            break;
                        case "Y":
                            y = Common.GetAttributeIntegerValue(messaging, sourceLineNumbers, attrib, 0, short.MaxValue);
                            break;
                        case "Width":
                            width = Common.GetAttributeIntegerValue(messaging, sourceLineNumbers, attrib, 0, short.MaxValue);
                            break;
                        case "Height":
                            height = Common.GetAttributeIntegerValue(messaging, sourceLineNumbers, attrib, 0, short.MaxValue);
                            break;
                        case "RightToLeft":
                            if (YesNoType.Yes == Common.GetAttributeYesNoValue(messaging, sourceLineNumbers, attrib))
                            {
                                attribs |= MsiInterop.MsidbControlAttributesRTLRO;
                            }
                            break;
                        case "RightAligned":
                            if (YesNoType.Yes == Common.GetAttributeYesNoValue(messaging, sourceLineNumbers, attrib))
                            {
                                attribs |= MsiInterop.MsidbControlAttributesRightAligned;
                            }
                            break;
                        case "LeftScroll":
                            if (YesNoType.Yes == Common.GetAttributeYesNoValue(messaging, sourceLineNumbers, attrib))
                            {
                                attribs |= MsiInterop.MsidbControlAttributesLeftScroll;
                            }
                            break;
                        default:
                            Common.UnexpectedAttribute(messaging, sourceLineNumbers, attrib);
                            break;
                    }
                }
                else
                {
                    Common.UnexpectedAttribute(messaging, sourceLineNumbers, attrib);
                }
            }

            text = Common.GetInnerText(node);

            if (String.IsNullOrEmpty(control) && 0 < attribs)
            {
                if (MsiInterop.MsidbControlAttributesRTLRO == (attribs & MsiInterop.MsidbControlAttributesRTLRO))
                {
                    messaging.Write(ErrorMessages.IllegalAttributeWithoutOtherAttributes(sourceLineNumbers, node.Name.ToString(), "RightToLeft", "Control"));
                }
                else if (MsiInterop.MsidbControlAttributesRightAligned == (attribs & MsiInterop.MsidbControlAttributesRightAligned))
                {
                    messaging.Write(ErrorMessages.IllegalAttributeWithoutOtherAttributes(sourceLineNumbers, node.Name.ToString(), "RightAligned", "Control"));
                }
                else if (MsiInterop.MsidbControlAttributesLeftScroll == (attribs & MsiInterop.MsidbControlAttributesLeftScroll))
                {
                    messaging.Write(ErrorMessages.IllegalAttributeWithoutOtherAttributes(sourceLineNumbers, node.Name.ToString(), "LeftScroll", "Control"));
                }
            }

            if (String.IsNullOrEmpty(control) && String.IsNullOrEmpty(dialog))
            {
                messaging.Write(ErrorMessages.ExpectedAttributesWithOtherAttribute(sourceLineNumbers, node.Name.ToString(), "Dialog", "Control"));
            }

            if (!messaging.EncounteredError)
            {
                LocalizedControl localizedControl = new LocalizedControl(dialog, control, x, y, width, height, attribs, text);
                string key = localizedControl.GetKey();
                if (localizedControls.ContainsKey(key))
                {
                    if (String.IsNullOrEmpty(localizedControl.Control))
                    {
                        messaging.Write(ErrorMessages.DuplicatedUiLocalization(sourceLineNumbers, localizedControl.Dialog));
                    }
                    else
                    {
                        messaging.Write(ErrorMessages.DuplicatedUiLocalization(sourceLineNumbers, localizedControl.Dialog, localizedControl.Control));
                    }
                }
                else
                {
                    localizedControls.Add(key, localizedControl);
                }
            }
        }
    }
}
