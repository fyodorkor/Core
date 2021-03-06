// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.WindowsInstaller.Bind
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using WixToolset.Data;
    using WixToolset.MergeMod;
    using WixToolset.Msi;
    using WixToolset.Core.Native;
    using WixToolset.Core.Bind;
    using WixToolset.Data.Tuples;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// Retrieve files information and extract them from merge modules.
    /// </summary>
    internal class ExtractMergeModuleFilesCommand
    {
        public ExtractMergeModuleFilesCommand(IMessaging messaging, IntermediateSection section, List<WixMergeTuple> wixMergeTuples)
        {
            this.Messaging = messaging;
            this.Section = section;
            this.WixMergeTuples = wixMergeTuples;
        }

        private IMessaging Messaging { get; }

        private IntermediateSection Section { get; }

        private List<WixMergeTuple> WixMergeTuples { get; }

        public IEnumerable<FileFacade> FileFacades { private get; set; }

        public int OutputInstallerVersion { private get; set; }

        public bool SuppressLayout { private get; set; }

        public string IntermediateFolder { private get; set; }

        public IEnumerable<FileFacade> MergeModulesFileFacades { get; private set; }

        public void Execute()
        {
            var mergeModulesFileFacades = new List<FileFacade>();

            IMsmMerge2 merge = MsmInterop.GetMsmMerge();

            // Index all of the file rows to be able to detect collisions with files in the Merge Modules.
            // It may seem a bit expensive to build up this index solely for the purpose of checking collisions
            // and you may be thinking, "Surely, we must need the file rows indexed elsewhere." It turns out
            // there are other cases where we need all the file rows indexed, however they are not common cases.
            // Now since Merge Modules are already slow and generally less desirable than .wixlibs we'll let
            // this case be slightly more expensive because the cost of maintaining an indexed file row collection
            // is a lot more costly for the common cases.
            var indexedFileFacades = this.FileFacades.ToDictionary(f => f.File.File, StringComparer.Ordinal);

            foreach (var wixMergeRow in this.WixMergeTuples)
            {
                bool containsFiles = this.CreateFacadesForMergeModuleFiles(wixMergeRow, mergeModulesFileFacades, indexedFileFacades);

                // If the module has files and creating layout
                if (containsFiles && !this.SuppressLayout)
                {
                    this.ExtractFilesFromMergeModule(merge, wixMergeRow);
                }
            }

            this.MergeModulesFileFacades = mergeModulesFileFacades;
        }

        private bool CreateFacadesForMergeModuleFiles(WixMergeTuple wixMergeRow, List<FileFacade> mergeModulesFileFacades, Dictionary<string, FileFacade> indexedFileFacades)
        {
            bool containsFiles = false;

            try
            {
                // read the module's File table to get its FileMediaInformation entries and gather any other information needed from the module.
                using (Database db = new Database(wixMergeRow.SourceFile, OpenDatabase.ReadOnly))
                {
                    if (db.TableExists("File") && db.TableExists("Component"))
                    {
                        Dictionary<string, FileFacade> uniqueModuleFileIdentifiers = new Dictionary<string, FileFacade>(StringComparer.OrdinalIgnoreCase);

                        using (View view = db.OpenExecuteView("SELECT `File`, `Directory_` FROM `File`, `Component` WHERE `Component_`=`Component`"))
                        {
                            // add each file row from the merge module into the file row collection (check for errors along the way)
                            while (true)
                            {
                                using (Record record = view.Fetch())
                                {
                                    if (null == record)
                                    {
                                        break;
                                    }

                                    // NOTE: this is very tricky - the merge module file rows are not added to the
                                    // file table because they should not be created via idt import.  Instead, these
                                    // rows are created by merging in the actual modules.
                                    var fileRow = new FileTuple(wixMergeRow.SourceLineNumbers, new Identifier(record[1], AccessModifier.Private));
                                    fileRow.File = record[1];
                                    fileRow.Compressed = wixMergeRow.FileCompression;
                                    //FileRow fileRow = (FileRow)this.FileTable.CreateRow(wixMergeRow.SourceLineNumbers, false);
                                    //fileRow.File = record[1];
                                    //fileRow.Compressed = wixMergeRow.FileCompression;

                                    var wixFileRow = new WixFileTuple(wixMergeRow.SourceLineNumbers);
                                    wixFileRow.Directory_ = record[2];
                                    wixFileRow.DiskId = wixMergeRow.DiskId;
                                    wixFileRow.PatchGroup = -1;
                                    wixFileRow.Source = new IntermediateFieldPathValue { Path = Path.Combine(this.IntermediateFolder, wixMergeRow.Id.Id, record[1]) };
                                    //WixFileRow wixFileRow = (WixFileRow)this.WixFileTable.CreateRow(wixMergeRow.SourceLineNumbers, false);
                                    //wixFileRow.Directory = record[2];
                                    //wixFileRow.DiskId = wixMergeRow.DiskId;
                                    //wixFileRow.PatchGroup = -1;
                                    //wixFileRow.Source = Path.Combine(this.IntermediateFolder, "MergeId.", wixMergeRow.Number.ToString(CultureInfo.InvariantCulture), record[1]);

                                    var mergeModuleFileFacade = new FileFacade(true, fileRow, wixFileRow);

                                    // If case-sensitive collision with another merge module or a user-authored file identifier.
                                    if (indexedFileFacades.TryGetValue(mergeModuleFileFacade.File.File, out var collidingFacade))
                                    {
                                        this.Messaging.Write(ErrorMessages.DuplicateModuleFileIdentifier(wixMergeRow.SourceLineNumbers, wixMergeRow.Id.Id, collidingFacade.File.File));
                                    }
                                    else if (uniqueModuleFileIdentifiers.TryGetValue(mergeModuleFileFacade.File.File, out collidingFacade)) // case-insensitive collision with another file identifier in the same merge module
                                    {
                                        this.Messaging.Write(ErrorMessages.DuplicateModuleCaseInsensitiveFileIdentifier(wixMergeRow.SourceLineNumbers, wixMergeRow.Id.Id, mergeModuleFileFacade.File.File, collidingFacade.File.File));
                                    }
                                    else // no collision
                                    {
                                        mergeModulesFileFacades.Add(mergeModuleFileFacade);

                                        // Keep updating the indexes as new rows are added.
                                        indexedFileFacades.Add(mergeModuleFileFacade.File.File, mergeModuleFileFacade);
                                        uniqueModuleFileIdentifiers.Add(mergeModuleFileFacade.File.File, mergeModuleFileFacade);
                                    }

                                    containsFiles = true;
                                }
                            }
                        }
                    }

                    // Get the summary information to detect the Schema
                    using (SummaryInformation summaryInformation = new SummaryInformation(db))
                    {
                        string moduleInstallerVersionString = summaryInformation.GetProperty(14);

                        try
                        {
                            int moduleInstallerVersion = Convert.ToInt32(moduleInstallerVersionString, CultureInfo.InvariantCulture);
                            if (moduleInstallerVersion > this.OutputInstallerVersion)
                            {
                                this.Messaging.Write(WarningMessages.InvalidHigherInstallerVersionInModule(wixMergeRow.SourceLineNumbers, wixMergeRow.Id.Id, moduleInstallerVersion, this.OutputInstallerVersion));
                            }
                        }
                        catch (FormatException)
                        {
                            throw new WixException(ErrorMessages.MissingOrInvalidModuleInstallerVersion(wixMergeRow.SourceLineNumbers, wixMergeRow.Id.Id, wixMergeRow.SourceFile, moduleInstallerVersionString));
                        }
                    }
                }
            }
            catch (FileNotFoundException)
            {
                throw new WixException(ErrorMessages.FileNotFound(wixMergeRow.SourceLineNumbers, wixMergeRow.SourceFile));
            }
            catch (Win32Exception)
            {
                throw new WixException(ErrorMessages.CannotOpenMergeModule(wixMergeRow.SourceLineNumbers, wixMergeRow.Id.Id, wixMergeRow.SourceFile));
            }

            return containsFiles;
        }

        private void ExtractFilesFromMergeModule(IMsmMerge2 merge, WixMergeTuple wixMergeRow)
        {
            bool moduleOpen = false;
            short mergeLanguage;

            var mergeId = wixMergeRow.Id.Id;

            try
            {
                mergeLanguage = Convert.ToInt16(wixMergeRow.Language, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                this.Messaging.Write(ErrorMessages.InvalidMergeLanguage(wixMergeRow.SourceLineNumbers, mergeId, wixMergeRow.Language.ToString()));
                return;
            }

            try
            {
                merge.OpenModule(wixMergeRow.SourceFile, mergeLanguage);
                moduleOpen = true;

                // extract the module cabinet, then explode all of the files to a temp directory
                string moduleCabPath = Path.Combine(this.IntermediateFolder, mergeId + ".cab");
                merge.ExtractCAB(moduleCabPath);

                string mergeIdPath = Path.Combine(this.IntermediateFolder, mergeId);
                Directory.CreateDirectory(mergeIdPath);

                try
                {
                    var cabinet = new Cabinet(moduleCabPath);
                    cabinet.Extract(mergeIdPath);
                }
                catch (FileNotFoundException)
                {
                    throw new WixException(ErrorMessages.CabFileDoesNotExist(moduleCabPath, wixMergeRow.SourceFile, mergeIdPath));
                }
                catch
                {
                    throw new WixException(ErrorMessages.CabExtractionFailed(moduleCabPath, wixMergeRow.SourceFile, mergeIdPath));
                }
            }
            catch (COMException ce)
            {
                throw new WixException(ErrorMessages.UnableToOpenModule(wixMergeRow.SourceLineNumbers, wixMergeRow.SourceFile, ce.Message));
            }
            finally
            {
                if (moduleOpen)
                {
                    merge.CloseModule();
                }
            }
        }
    }
}
