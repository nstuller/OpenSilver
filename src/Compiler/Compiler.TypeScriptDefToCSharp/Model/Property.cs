﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TypeScriptDefToCSharp.Model
{
    public class Property : Variable
    {
        public Property(Container<Declaration> super)
            : base(super)
        {
        }

        public Property(XElement elem, Container<Declaration> super, TypeScriptDefContext context)
            : base(elem, super, context)
        {
        }

        public override string ToString()
        {
            var res = new StringBuilder();
            if (this.Type != null)
            {
                // Get the property type
                string type;

                if (this.Type != null && this.Type.ToString() != String.Empty)
                    type = this.Type.ToString();
                else
                    type = "IJSObject";

                // Get if the property is static
                string staticState = this.Static ? "static " : "";

                string instanceName;
                string prefix;

                string classPath = "";
                if (this.Super is Class && this.Super != null)
                {
                    if (this.Super.Super != null)
                        classPath = this.Super.Super.SkippedFullName(".", 1);
                }

                if (this.Static)
                {
                    instanceName = "\"\"";
                    if (String.IsNullOrWhiteSpace(classPath))
                        prefix = "";
                    else
                        prefix = classPath + ".";
                }
                else
                {
                    prefix = "$0.";
                    instanceName = "this.UnderlyingJSInstance";
                }

                res.AppendLine("public " + staticState + type + " " + Tool.ClearKeyWord(this.Name));
                res.AppendLine("{");

                // Getter
                res.AppendLine("get");
                res.AppendLine("{");
                res.AppendLine("var jsObj = Interop.ExecuteJavaScript(\"" + prefix + this.Name + "\", " + instanceName + ");");
                res.AppendLine("if (jsObj == JSObject.Undefined.UnderlyingJSInstance)");
                res.AppendLine("    return " + this.GetterDefaultValue(type) + ";");
                res.AppendLine("else");
                res.AppendLine("    return " + this.Type.New("jsObj") + ";");
                res.AppendLine("}");

                // Setter
                res.AppendLine("set");
                res.AppendLine("{");
                res.AppendLine("Interop.ExecuteJavaScript(\"" + prefix + this.Name + " = $1\", " + instanceName + ",");
                res.AppendLine("    " + this.SetterValue(type));
                res.AppendLine(");");
                res.AppendLine("}");

                res.AppendLine("}");
            }
            return res.ToString();
        }

        private string GetterDefaultValue(string type)
        {
            var res = new StringBuilder();
            switch (type)
            {
                case "double":
                    res.Append("0");
                    break;
                case "bool":
                    res.Append("false");
                    break;
                default:
                    res.Append("null");
                    break;
            }
            return res.ToString();
        }

        private string SetterValue(string type)
        {
            var res = new StringBuilder();
            switch(type)
            {
                case "double":
                case "string":
                case "bool":
                    res.Append("value");
                    break;
                default:
                    if (this.Type is Model.Enum)
                        res.Append("\"" + type + ".\" + value.ToString()");
                    else if (this.Type is FunctionType)
                    {
                        var Type = (FunctionType)this.Type;

                        res.AppendLine("value != null ?");

                        FunctionType jsFuncType = new FunctionType();
                        Param objectParam = new Param(null) { Type = BasicType.Object };
                        for (int i = 0; i < Type.Params.Count; i++)
                            jsFuncType.Params.Add(objectParam);
                        if (Type.ReturnType != null)
                            jsFuncType.ReturnType = BasicType.Object;

                        res.Append("    new " + jsFuncType.ToString() + "((");
                        for (int i = 0; i < Type.Params.Count; ++i)
                        {
                            if (i > 0)
                                res.Append(", ");
                            res.Append("jsArg" + (i + 1));
                        }
                        res.AppendLine(") =>");
                        res.AppendLine("    {");
                        res.Append("        ");
                        if (Type.ReturnType != null)
                            res.Append("return ");
                        res.AppendLine("value(");
                        for (int i = 0; i < Type.Params.Count; ++i)
                        {
                            if (i > 0)
                                res.AppendLine(",");
                            res.Append("            " + Type.Params[i].Type.New("jsArg" + (i + 1)));
                        }
                        res.AppendLine();
                        res.AppendLine("        );");
                        res.AppendLine("    })");

                        res.Append("    : JSObject.Undefined.UnderlyingJSInstance");
                    }
                    else
                        res.Append("value.ToJavaScriptObject()");
                    break;
            }
            return res.ToString();
        }
    }
}