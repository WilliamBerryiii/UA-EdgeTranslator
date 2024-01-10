﻿namespace Opc.Ua.Edge.Translator
{
    using Newtonsoft.Json;
    using Opc.Ua.Edge.Translator.Models;
    using Serilog;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.RegularExpressions;

    public class WoT2DTDLMapper
    {
        public static string WoT2DTDL(string contents)
        {
            // Map WoT Thing Description to DTDL device model equivalent
            try
            {
                ThingDescription td = JsonConvert.DeserializeObject<ThingDescription>(contents);

                DTDL dtdl = new();
                dtdl.Context = "dtmi:dtdl:context;2";
                dtdl.Id = "dtmi:" + td.Title.ToLowerInvariant().Replace(' ', ':') + ";1";
                dtdl.Type = "Interface";
                dtdl.DisplayName = td.Name;
                dtdl.Description = td.Title;
                dtdl.Comment = td.Base;

                foreach (Uri context in td.Context)
                {
                    if (context.OriginalString.Contains("/UA/"))
                    {
                        dtdl.Comment += ";" + context.OriginalString;
                    }
                }

                if (!string.IsNullOrEmpty(td.OpcUaObjectNode))
                {
                    dtdl.Comment += $";nodeId:{td.OpcUaObjectNode}";
                }

                if (!string.IsNullOrWhiteSpace(td.OpcUaParentNode))
                {
                    dtdl.Comment += $";parent:{td.OpcUaObjectNode}";
                }

                if (!string.IsNullOrWhiteSpace(td.OpcUaObjectType))
                {
                    dtdl.Comment += $";type:{td.OpcUaObjectType}";
                }

                dtdl.Contents = new List<Content>();
                foreach (KeyValuePair<string, Property> property in td.Properties)
                {
                    foreach (object form in property.Value.Forms)
                    {
                        if (td.Base.ToLower().StartsWith("modbus+tcp://"))
                        {
                            ModbusForm modbusForm = JsonConvert.DeserializeObject<ModbusForm>(form.ToString());
                            Content content = new();
                            content.Type = "Telemetry";
                            content.Name = modbusForm.Href;
                            content.DisplayName = property.Key;
                            content.Description = modbusForm.ModbusEntity.ToString().ToLower() + ";" + modbusForm.ModbusPollingTime.ToString();
                            content.Comment = modbusForm.OpcUaType;
                            if (!string.IsNullOrWhiteSpace(modbusForm.OpcUaVariableNode))
                            {
                                content.Comment += $";nodeId:{modbusForm.OpcUaVariableNode}";
                            }

                            switch (modbusForm.ModbusType)
                            {
                                case ModbusType.Float: content.Schema = "float"; break;
                                default: content.Schema = "float"; break;
                            }

                            dtdl.Contents.Add(content);
                        }
                    }
                }

                return JsonConvert.SerializeObject(dtdl, Formatting.Indented);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex.Message, ex);
                return string.Empty;
            }
        }

        public static string DTDL2WoT(string contents)
        {
            // Map DTDL device model to WoT Thing Description equivalent
            try
            {
                DTDL dtdl = JsonConvert.DeserializeObject<DTDL>(contents);

                ThingDescription td = new();

                List<Uri> context = new()
                {
                    new Uri("https://www.w3.org/2019/wot/td/v1", UriKind.Absolute)
                };

                string[] comments = SplitWithNodeIds(';', dtdl.Comment);
                if ((comments != null) && (comments.Length > 1))
                {
                    for (var i = 1; i < comments.Length; i++)
                    {
                        var comment = comments[i];
                        Log.Logger.Debug(comment);

                        if (comment.StartsWith("nodeId:"))
                        {
                            td.OpcUaObjectNode = comment.Substring(comment.IndexOf(":") + 1);
                        }
                        else if (comment.StartsWith("parent:"))
                        {
                            td.OpcUaParentNode = comment.Substring(comment.IndexOf(":") + 1);
                        }
                        else if (comment.StartsWith("type:"))
                        {
                            td.OpcUaObjectType = comment.Substring(comment.IndexOf(":") + 1);
                        }
                        else
                        {
                            context.Add(new Uri(comment, UriKind.Absolute));
                        }
                    }
                }

                td.Context = context.ToArray();

                string[] idParts = dtdl.Id.Split(":");
                if (idParts != null && idParts.Length > 1)
                {
                    string deviceID = idParts[idParts.Length - 1];
                    deviceID = deviceID.Substring(0, deviceID.IndexOf(';'));
                    td.Id = "urn:" + deviceID;
                }

                td.SecurityDefinitions = new();
                td.SecurityDefinitions.NosecSc = new();
                td.SecurityDefinitions.NosecSc.Scheme = "nosec";
                td.Security = new List<string>(){ "nosec_sc" }.ToArray();
                td.Type = new List<string>() { "Thing" }.ToArray();
                td.Name = dtdl.DisplayName;
                td.Title = dtdl.Description;

                if ((comments != null) && (comments.Length > 0))
                {
                    td.Base = comments[0];
                }

                td.Properties = new Dictionary<string, Property>();
                foreach (Content content in dtdl.Contents)
                {
                    Property property = new();
                    property.Type = TypeEnum.Number;
                    property.ReadOnly = true;
                    property.Observable = true;

                    if ((comments != null) && (comments.Length > 0) && comments[0].StartsWith("modbus+tcp://"))
                    {
                        ModbusForm form = new();
                        form.Href = content.Name;
                        form.Op = new List<Op>() { Op.Readproperty, Op.Observeproperty }.ToArray();
                        var uaData = SplitWithNodeIds(';', content.Comment);
                        form.OpcUaType = uaData?.FirstOrDefault(d => d.StartsWith("nsu"));
                        var nodeId = uaData?.FirstOrDefault(d => d.StartsWith("nodeId:"));
                        form.OpcUaVariableNode = nodeId?.Substring(nodeId.IndexOf(":") + 1);

                        switch (content.Schema)
                        {
                            case "float": form.ModbusType = ModbusType.Float; break;
                            default: form.ModbusType = ModbusType.Float; break;
                        }

                        string[] descriptionParts = content.Description.Split(';');
                        if ((descriptionParts != null) && (descriptionParts.Length > 0) && descriptionParts[0] == "holdingregister")
                        {
                            form.ModbusEntity = ModbusEntity.Holdingregister;
                        }

                        if ((descriptionParts != null) && (descriptionParts.Length > 1) && long.TryParse(descriptionParts[1], out long result))
                        {
                            form.ModbusPollingTime = long.Parse(descriptionParts[1]);
                        }

                        property.Forms = new List<ModbusForm>() { form }.ToArray();
                    }

                    td.Properties.Add(content.DisplayName, property);
                }

                return JsonConvert.SerializeObject(td, Formatting.Indented, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex.Message, ex);
                return string.Empty;
            }
        }

        private static string[] SplitWithNodeIds(char separator, string content) => Regex.Split(content, $"{separator}(?![sigb]=)", RegexOptions.None);
    }
}
