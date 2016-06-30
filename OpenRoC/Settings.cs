﻿namespace oroc
{
    using System;
    using System.IO;
    using System.Xml.Linq;

    public sealed class Settings
    {
        private static volatile Settings instance;
        private XElement openrocRoot;
        private XElement optionsRoot;
        private bool dirty = false;

        public static Settings Instance
        {
            get
            {
                if (instance == null)
                {
                    if (instance == null)
                        instance = new Settings();
                }

                return instance;
            }
        }

        Settings()
        {
            FileInfo config_file = new FileInfo(Path.Combine(
                Program.Directory, Properties.Resources.SettingsFileName));

            if (!config_file.Exists)
            {
                Log.w("Config file does not exist. It will be created at {0}", config_file.FullName);
                File.WriteAllText(config_file.FullName, Properties.Resources.SettingsBaseXml);
            }

            try
            {
                openrocRoot = XElement.Load(config_file.FullName);
                Log.d("Succesfully loaded config file {0}", config_file.FullName);
                dirty = false;
            }
            catch (Exception ex)
            {
                openrocRoot = XElement.Parse(Properties.Resources.SettingsBaseXml);
                Log.w("Failed to load config file. Defaults will be used: Reason: {0}", ex.Message);
                dirty = true;
            }

            if (openrocRoot == null || openrocRoot.Name != Properties.Resources.SettingsRootNode)
            {
                openrocRoot = new XElement(Properties.Resources.SettingsRootNode);
                Log.w("Config root name is different than {0}", Properties.Resources.SettingsRootNode);
                dirty = true;
            }

            optionsRoot = openrocRoot.Element(Properties.Resources.SettingsOptionNode);

            if (optionsRoot == null)
            {
                Log.w("Options root name is empty. A node will be created for it.");
                optionsRoot = new XElement(Properties.Resources.SettingsOptionNode);
                openrocRoot.Add(optionsRoot);
                dirty = true;
            }

            Save();
        }

        public void Write<T>(string node, T value) where T : new()
        {
            try
            {
                if (optionsRoot.Element(node) == null)
                    optionsRoot.Add(new XElement(node));

                optionsRoot.Element(node).ReplaceAll(
                    XElement.Parse(value.ToXmlNodeString()).Elements());

                dirty = true;
            }
            catch (Exception ex)
            {
                Log.e("Failed to write XML string for node: {0}. Reason: {1}", node, ex.Message);
                dirty = false;
            }
        }

        public T Read<T>(string node) where T : new()
        {
            try
            {
                if (optionsRoot.Element(node) != null)
                    return Extensions.FromXmlNodeString<T>(
                        optionsRoot.Element(node).ToString(), node);
            }
            catch(Exception ex)
            {
                Log.e("Failed to read XML string from node: {0}. Reason: {1}", node, ex.Message);
            }

            return new T();
        }

        public void Save()
        {
            if (!dirty)
                return;

            openrocRoot.Save(Path.Combine(
                Program.Directory,
                Properties.Resources.SettingsFileName));

            dirty = false;
        }
    }
}
