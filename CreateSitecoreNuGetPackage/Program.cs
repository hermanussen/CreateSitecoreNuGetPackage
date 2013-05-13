/*
    CreateSitecoreNuGetPackage Sitecore module
    Copyright (C) 2013  Robin Hermanussen

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Sitecore.Data;
using Sitecore.Data.Proxies;
using Sitecore.Data.Serialization.ObjectModel;
using Sitecore.Install;
using Sitecore.Install.Zip;
using Sitecore.SecurityModel;
using Sitecore.Xml;
using Sitecore.Zip;

namespace CreateSitecoreNuGetPackage
{
    public class Program
    {
        private static FileInfo file;

        static void Main(string[] args)
        {
            // check if the arguments are available and valid

            if (args.Length <= 0)
            {
                Console.WriteLine("Command line tool to convert a regular Sitecore package into a NuGet package");
                Console.WriteLine("Usage: {0} [PATH_TO_SITECORE_PACKAGE]", typeof(Program).Namespace);
                return;
            }

            file = new FileInfo(args[0]);
            if (! file.Exists)
            {
                Console.WriteLine("The file '{0}' could not be found. Please provide a valid path", args[0]);
                Console.WriteLine("Usage: {0} [PATH_TO_SITECORE_PACKAGE]", typeof(Program).Namespace);
                return;
            }
            
            // determine the name of the package (should exclude the version number
            string nuGetPackageName = Path.GetFileNameWithoutExtension(file.Name);
            if (! string.IsNullOrWhiteSpace(nuGetPackageName) && nuGetPackageName.IndexOf('-') > 0)
            {
                nuGetPackageName = nuGetPackageName.Substring(0, nuGetPackageName.IndexOf('-'));
            }

            // the target package directory
            DirectoryInfo nuGetPackageDir = new DirectoryInfo(string.Format("{0}\\{1}", file.Directory.FullName, nuGetPackageName));

            // fail if the directory already exists
            if (nuGetPackageDir.Exists)
            {
                Console.WriteLine("'{0}' package directory already exists - please remove the directory before running this tool", nuGetPackageDir);
                return;
            }

            Console.WriteLine("Creating NuGet package '{0}'", nuGetPackageName);
            
            // Create the initial folder structure
            nuGetPackageDir.Create();
            DirectoryInfo nuGetPackageContentDir = new DirectoryInfo(string.Format("{0}\\content", nuGetPackageDir.FullName));
            nuGetPackageContentDir.Create();
            DirectoryInfo nuGetPackageSerializationDir = new DirectoryInfo(string.Format("{0}\\serialization", nuGetPackageDir.FullName));
            nuGetPackageSerializationDir.Create();
            DirectoryInfo nuGetPackageToolsDir = new DirectoryInfo(string.Format("{0}\\tools", nuGetPackageDir.FullName));
            nuGetPackageToolsDir.Create();
            DirectoryInfo nuGetPackageWwwrootDir = new DirectoryInfo(string.Format("{0}\\wwwroot", nuGetPackageDir.FullName));
            nuGetPackageWwwrootDir.Create();

            // Copy the package itself to the content folder
            file.CopyTo(string.Format("{0}\\{1}", nuGetPackageContentDir.FullName, file.Name));

            // Serialize the items from the package into the NuGet package
            SerializeItems(LoadItems(), nuGetPackageSerializationDir);
            
            // Copy files such as layouts, DLL's etc. to the wwwroot folder
            CopyFilesFromPackage(nuGetPackageWwwrootDir);

            // Copy the scripts that are needed to deserialize the items into Sitecore
            CopyTools(nuGetPackageToolsDir);

            // Create a specification file that can be used to generate the uploadable package
            CreateNuSpec(nuGetPackageName, nuGetPackageDir);

            Console.WriteLine("NuGet package created at '{0}'", nuGetPackageDir.FullName);
        }

        /// <summary>
        /// Loads items from the Sitecore package and returns a list of paths and their corresponding items.
        /// </summary>
        /// <returns></returns>
        public static List<KeyValuePair<string, SyncItem>> LoadItems()
        {
            List<KeyValuePair<string, SyncItem>> items = new List<KeyValuePair<string, SyncItem>>();

            ApplyToZipEntries(entryData =>
            {
                if (entryData.Key.EndsWith("/xml") && !entryData.Key.StartsWith("properties/"))
                {
                    try
                    {
                        string xml = new StreamReader(entryData.GetStream().Stream, Encoding.UTF8).ReadToEnd();
                        if (string.IsNullOrWhiteSpace(xml))
                        {
                            return;
                        }
                        XmlDocument document = XmlUtil.LoadXml(xml);
                        if (document == null)
                        {
                            return;
                        }
                        SyncItem loadedItem = LoadItem(document);
                        if (loadedItem != null)
                        {
                            items.Add(new KeyValuePair<string, SyncItem>(entryData.Key, loadedItem));
                        }
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("Unable to load xml from file {0}: {1}", entryData.Key, exc.Message);
                    }
                }
            });

            return items;
        }

        /// <summary>
        /// Serializes the items that are passed in to a target folder using Sitecore serialization.
        /// </summary>
        /// <param name="items"></param>
        /// <param name="nuGetPackageSerializationDir"></param>
        private static void SerializeItems(IEnumerable<KeyValuePair<string, SyncItem>> items, DirectoryInfo nuGetPackageSerializationDir)
        {
            foreach (KeyValuePair<string, SyncItem> syncItem in items)
            {
                IEnumerable<string> path = syncItem.Key.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Skip(1);
                var idPathPart = path.Select((k, i) => new { k = k, i = i }).FirstOrDefault(p => ID.IsID(p.k));
                if (idPathPart != null)
                {
                    path = path.Take(idPathPart.i);
                }

                FileInfo targetFile =
                    new FileInfo(string.Format("{0}\\{1}.item", nuGetPackageSerializationDir.FullName, string.Join("\\", path)));
                if (!targetFile.Directory.Exists)
                {
                    targetFile.Directory.Create();
                }
                using (TextWriter writer = new StreamWriter(targetFile.FullName))
                {
                    syncItem.Value.Serialize(writer);
                }
            }
        }

        /// <summary>
        /// Copies the static files in a package to the wwwroot folder.
        /// </summary>
        /// <param name="nuGetPackageWwwrootDir"></param>
        private static void CopyFilesFromPackage(DirectoryInfo nuGetPackageWwwrootDir)
        {
            const string filesPrefix = "files/";
            ApplyToZipEntries(entryData =>
            {
                if (entryData.Key.StartsWith(filesPrefix) && !entryData.Key.EndsWith("//"))
                {
                    FileInfo targetFile =
                        new FileInfo(string.Format("{0}\\{1}", nuGetPackageWwwrootDir.FullName,
                                                   entryData.Key.Substring(filesPrefix.Length)));
                    if (!targetFile.Directory.Exists)
                    {
                        targetFile.Directory.Create();
                    }
                    BinaryWrite(entryData.GetStream().Stream, targetFile.FullName);
                }
            });
        }

        /// <summary>
        /// Copies the tools that are embedded in this exe to the tools folder.
        /// These tools are used by NuGet to deserialize items into Sitecore when installing.
        /// </summary>
        /// <param name="nuGetPackageToolsDir"></param>
        private static void CopyTools(DirectoryInfo nuGetPackageToolsDir)
        {
            string[] fileNames = new[] { "init.ps1", "install.ps1", "Sitecore.NuGet.1.0.dll", "uninstall.ps1" };
            foreach (string fileName in fileNames)
            {
                Stream inputStream =
                    Assembly.GetExecutingAssembly()
                            .GetManifestResourceStream(string.Format("CreateSitecoreNuGetPackage.tools.{0}", fileName));
                string targetFile = string.Format("{0}\\{1}", nuGetPackageToolsDir.FullName, fileName);
                BinaryWrite(inputStream, targetFile);
            }
        }

        /// <summary>
        /// Creates the initial .nuspec file that has info about all the files and the metadata.
        /// </summary>
        /// <param name="nuGetPackageName"></param>
        /// <param name="nuGetPackageDir"></param>
        private static void CreateNuSpec(string nuGetPackageName, DirectoryInfo nuGetPackageDir)
        {
            Dictionary<string, string> metaDataMappings = new Dictionary<string, string>()
                {
                    {"author", string.Empty},
                    {"comment", string.Empty},
                    {"license", string.Empty},
                    {"name", string.Empty},
                    {"publisher", string.Empty},
                    {"readme", string.Empty},
                    {"revision", string.Empty},
                    {"version", string.Empty}
                };
            ApplyToZipEntries(entryData =>
                {
                    string matchingKey =
                        metaDataMappings.Keys.FirstOrDefault(
                            key => string.Format("metadata/sc_{0}.txt", key).Equals(entryData.Key));
                    if (matchingKey == null)
                    {
                        return;
                    }
                    using (StreamReader reader = new StreamReader(entryData.GetStream().Stream))
                    {
                        metaDataMappings[matchingKey] = reader.ReadToEnd();
                    }
                });
            if (string.IsNullOrWhiteSpace(metaDataMappings["comment"]))
            {
                metaDataMappings["comment"] = nuGetPackageName;
            }

            const string xmlNs = "http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd";
            const string marketUrl = "http://marketplace.sitecore.net/";
            XDocument nuSpecFile = new XDocument(new XElement(XName.Get("package", xmlNs)));
            
            // add metadata section
            nuSpecFile.Root.Add(new XElement(XName.Get("metadata", xmlNs),
                                             new XElement(XName.Get("id", xmlNs), nuGetPackageName.Replace(" ", string.Empty)),
                                             new XElement(XName.Get("version", xmlNs),
                                                          string.Format("{0} {1}", metaDataMappings["version"],
                                                                        metaDataMappings["revision"]).Trim()),
                                             new XElement(XName.Get("title", xmlNs), metaDataMappings["name"]),
                                             new XElement(XName.Get("authors", xmlNs), metaDataMappings["author"]),
                                             new XElement(XName.Get("owners", xmlNs), metaDataMappings["publisher"]),
                                             new XElement(XName.Get("licenseUrl", xmlNs), marketUrl),
                                             new XElement(XName.Get("projectUrl", xmlNs), marketUrl),
                                             new XElement(XName.Get("description", xmlNs), metaDataMappings["comment"])));

            // add files section
            XElement filesElement = new XElement(XName.Get("files", xmlNs));
            foreach (string fileName in Directory.EnumerateFiles(nuGetPackageDir.FullName, "*", SearchOption.AllDirectories))
            {
                string shortFileName = fileName.Substring(nuGetPackageDir.FullName.Length + 1);
                filesElement.Add(new XElement(XName.Get("file", xmlNs),
                    new XAttribute("src", shortFileName),
                    new XAttribute("target", shortFileName)));
            }
            nuSpecFile.Root.Add(filesElement);

            nuSpecFile.Save(string.Format("{0}\\{1}.nuspec", nuGetPackageDir.FullName, nuGetPackageName));
        }

        /// <summary>
        /// Writes a binary stream to the target file.
        /// </summary>
        /// <param name="inputStream"></param>
        /// <param name="targetFile"></param>
        private static void BinaryWrite(Stream inputStream, string targetFile)
        {
            using (Stream stream = inputStream)
            {
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    using (BinaryWriter writer = new BinaryWriter(new FileInfo(targetFile).Create()))
                    {
                        const int bufferLength = 128;
                        byte[] buffer = new byte[bufferLength];
                        int bytesRead;
                        do
                        {
                            bytesRead = reader.Read(buffer, 0, bufferLength);
                            writer.Write(buffer, 0, bytesRead);
                        }
                        while (bytesRead != 0);
                    }
                }
            }
        }

        /// <summary>
        /// Loads an item as defined in XML from the package.
        /// </summary>
        /// <param name="document"></param>
        /// <returns></returns>
        private static SyncItem LoadItem(XmlDocument document)
        {
            SyncItem loadedItem = new SyncItem();

            XmlNode itemNode = document.DocumentElement;
            loadedItem.ID = XmlUtil.GetAttribute("id", itemNode);
            loadedItem.Name = XmlUtil.GetAttribute("name", itemNode);
            loadedItem.ParentID = XmlUtil.GetAttribute("parentid", itemNode);
            loadedItem.TemplateID = XmlUtil.GetAttribute("tid", itemNode);
            loadedItem.MasterID = XmlUtil.GetAttribute("mid", itemNode);
            loadedItem.BranchId = XmlUtil.GetAttribute("bid", itemNode);
            loadedItem.TemplateName = XmlUtil.GetAttribute("template", itemNode);

            SyncVersion loadedVersion = loadedItem.AddVersion(
                XmlUtil.GetAttribute("language", itemNode),
                XmlUtil.GetAttribute("version", itemNode),
                string.Empty);

            foreach (XmlNode node in itemNode.SelectNodes("fields/field"))
            {
                XmlNode content = node.SelectSingleNode("content");
                loadedVersion.AddField(
                    XmlUtil.GetAttribute("tfid", node),
                    XmlUtil.GetAttribute("key", node),
                    XmlUtil.GetAttribute("key", node),
                    content != null ? XmlUtil.GetValue(content) : null,
                    content != null);
            }
            return loadedItem;
        }

        /// <summary>
        /// Performs the action that is passed while reading the Sitecore package.
        /// </summary>
        /// <param name="action"></param>
        private static void ApplyToZipEntries(Action<ZipEntryData> action)
        {
            using (new SecurityDisabler())
            {
                using (new ProxyDisabler())
                {
                    ZipReader reader = new ZipReader(file.FullName, Encoding.UTF8);
                    ZipEntry entry = reader.GetEntry("package.zip");

                    using (MemoryStream stream = new MemoryStream())
                    {
                        StreamUtil.Copy(entry.GetStream(), stream, 0x4000);

                        reader = new ZipReader(stream);

                        foreach (ZipEntryData entryData in reader.Entries.Select(zipEntry => new ZipEntryData(zipEntry)))
                        {
                            action(entryData);
                        }
                    }
                }
            }
        }
    }
}
