﻿
/*===================================================================================
* 
*   Copyright (c) Userware (OpenSilver.net, CSHTML5.com)
*      
*   This file is part of both the OpenSilver Compiler (https://opensilver.net), which
*   is licensed under the MIT license (https://opensource.org/licenses/MIT), and the
*   CSHTML5 Compiler (http://cshtml5.com), which is dual-licensed (MIT + commercial).
*   
*   As stated in the MIT license, "the above copyright notice and this permission
*   notice shall be included in all copies or substantial portions of the Software."
*  
\*====================================================================================*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mono.Cecil;
using OpenSilver.Compiler.OtherHelpersAndHandlers.MonoCecilAssembliesInspector;
using OpenSilver.Internal;

namespace OpenSilver.Compiler
{
    internal static partial class GeneratingCSCode
    {
        private class GeneratorPass2 : ICodeGenerator
        {
            private abstract class GeneratorScope
            {
                protected GeneratorScope(string rootElement)
                {
                    Root = rootElement;
                    XamlContext = GeneratingUniqueNames.GenerateUniqueNameFromString("xamlContext");
                }

                public string Root { get; }

                public string XamlContext { get; }

                public StringBuilder StringBuilder { get; } = new StringBuilder();

                public abstract void RegisterName(string name, string scopedElement);
                
                protected abstract string ToStringCore();
                
                public sealed override string ToString() => ToStringCore();
            }

            private sealed class RootScope : GeneratorScope
            {
                private readonly Dictionary<string, string> _namescope;

                public RootScope(string rootElementName, bool createNameScope) 
                    : base(rootElementName) 
                {
                    StringBuilder.AppendLine($"var {XamlContext} = {RuntimeHelperClass}.Create_XamlContext();");
                    if (createNameScope)
                    {
                        _namescope = new Dictionary<string, string>();
                    }
                }

                public override void RegisterName(string name, string scopedElement)
                {
                    if (_namescope != null)
                    {
                        _namescope.Add(name, scopedElement);
                    }
                }

                protected override string ToStringCore()
                {
                    StringBuilder builder = new StringBuilder();

                    builder.Append(StringBuilder.ToString());
                    AppendNamescope(builder);

                    return builder.ToString();
                }

                private void AppendNamescope(StringBuilder builder)
                {
                    if (_namescope == null) return;

                    builder.AppendLine($"{RuntimeHelperClass}.InitializeNameScope({Root});");

                    foreach (var kp in _namescope)
                    {
                        builder.AppendLine($"{RuntimeHelperClass}.RegisterName({Root}, {EscapeString(kp.Key)}, {kp.Value});");
                    }
                }
            }

            private sealed class NewObjectScope : GeneratorScope
            {
                public NewObjectScope(string objectName, string objectType)
                    : base(objectName)
                {
                    ObjectType = objectType;
                    MethodName = $"New_{objectName}";
                }

                public string MethodName { get; }

                public string ObjectType { get; }

                public override void RegisterName(string name, string scopedElement)
                {
                    throw new NotSupportedException();
                }

                protected override string ToStringCore()
                {
                    StringBuilder builder = new StringBuilder();

                    builder.AppendLine($"private static {ObjectType} {MethodName}({XamlContextClass} {XamlContext})")
                        .AppendLine("{")
                        .Append(StringBuilder.ToString());
                    builder.AppendLine($"return {Root};")
                        .AppendLine("}");

                    return builder.ToString();
                }
            }

            private sealed class FrameworkTemplateScope : GeneratorScope
            {
                private readonly IMetadata _metadata;

                public FrameworkTemplateScope(string templateName, string templateRoot, IMetadata metadata)
                    : base(templateRoot)
                {
                    _metadata = metadata;
                    Name = templateName;
                    TemplateOwner = $"templateOwner_{templateName}";
                    MethodName = $"Create_{templateName}";
                }

                public string Name { get; }

                public string TemplateOwner { get; }

                public string MethodName { get; }

                public override void RegisterName(string name, string scopedElement)
                {
                    StringBuilder.AppendLine($"{RuntimeHelperClass}.XamlContext_RegisterName({XamlContext}, {EscapeString(name)}, {scopedElement});");
                }

                protected override string ToStringCore()
                {
                    StringBuilder builder = new StringBuilder();

                    builder.AppendLine($"private static global::{_metadata.SystemWindowsNS}.IFrameworkElement {MethodName}(global::{_metadata.SystemWindowsNS}.IFrameworkElement {TemplateOwner}, {XamlContextClass} {XamlContext})")
                        .AppendLine("{")
                        .Append(StringBuilder.ToString());
                    builder.AppendLine($"return {Root};")
                        .AppendLine("}");

                    return builder.ToString();
                }
            }

            private class GeneratorContext
            {
                private readonly Stack<GeneratorScope> _scopes = new();

                public readonly List<string> ResultingMethods = new List<string>();
                public readonly List<string> ResultingFieldsForNamedElements = new List<string>();
                public readonly List<string> ResultingFindNameCalls = new List<string>();
                public readonly ComponentConnectorBuilderCS ComponentConnector = new ComponentConnectorBuilderCS();

                public bool GenerateFieldsForNamedElements { get; set; }

                public GeneratorScope CurrentScope => _scopes.Peek();
                public StringBuilder StringBuilder => CurrentScope.StringBuilder;

                public void PushScope(GeneratorScope scope)
                {
                    _scopes.Push(scope);
                }

                public void PopScope()
                {
                    if (_scopes.Count <= 1)
                    {
                        throw new InvalidOperationException();
                    }

                    ResultingMethods.Add(_scopes.Pop().ToString());
                }

                public string CurrentXamlContext => CurrentScope.XamlContext;
            }

            private readonly XamlReader _reader;
            private readonly ConversionSettings _settings;

            private readonly string _sourceFile;
            private readonly string _fileNameWithPathRelativeToProjectRoot;
            private readonly string _assemblyNameWithoutExtension;
            private readonly AssembliesInspector _reflectionOnSeparateAppDomain;
            private readonly string _outputResourcesPath;

            public GeneratorPass2(XDocument doc,
                string sourceFile,
                string fileNameWithPathRelativeToProjectRoot,
                string assemblyNameWithoutExtension,
                AssembliesInspector reflectionOnSeparateAppDomain,
                ConversionSettings settings,
                string outputResourcesPath)
            {
                _reader = new XamlReader(doc);
                _settings = settings;
                _sourceFile = sourceFile;
                _fileNameWithPathRelativeToProjectRoot = fileNameWithPathRelativeToProjectRoot;
                _assemblyNameWithoutExtension = assemblyNameWithoutExtension;
                _reflectionOnSeparateAppDomain = reflectionOnSeparateAppDomain;
                _outputResourcesPath = outputResourcesPath;
            }

            public string Generate() => GenerateImpl(new GeneratorContext());

            private string GenerateImpl(GeneratorContext parameters)
            {
                parameters.GenerateFieldsForNamedElements = 
                    !_reflectionOnSeparateAppDomain.IsAssignableFrom(
                        _settings.Metadata.SystemWindowsNS, "ResourceDictionary",
                        _reader.Document.Root.Name.NamespaceName, _reader.Document.Root.Name.LocalName) 
                    &&
                    !_reflectionOnSeparateAppDomain.IsAssignableFrom(
                        _settings.Metadata.SystemWindowsNS, "Application",
                        _reader.Document.Root.Name.NamespaceName, _reader.Document.Root.Name.LocalName);

                parameters.PushScope(
                    new RootScope(GeneratingCode.GetUniqueName(_reader.Document.Root),
                        _reflectionOnSeparateAppDomain.IsAssignableFrom(
                            _settings.Metadata.SystemWindowsNS, "IFrameworkElement",
                            _reader.Document.Root.Name.NamespaceName, _reader.Document.Root.Name.LocalName)
                    )
                );

                // Traverse the tree in "post order" (ie. start with child elements then traverse parent elements):
                while (_reader.Read())
                {
                    switch (_reader.NodeType)
                    {
                        case XamlNodeType.StartObject:
                            TryCatch(OnWriteStartObject, parameters);
                            break;

                        case XamlNodeType.EndObject:
                            TryCatch(OnWriteEndObject, parameters);
                            break;

                        case XamlNodeType.StartMember:
                            TryCatch(OnWriteStartMember, parameters);
                            break;

                        case XamlNodeType.EndMember:
                            if (_reader.MemberData.Member != null)
                            {
                                TryCatch(OnWriteEndMember, parameters);
                            }
                            else
                            {
                                TryCatch(OnWriteEndMemberCollection, parameters);
                            }
                            break;
                    }
                }

                // Get general information about the class:
                GetClassInformationFromXaml(_reader.Document, _reflectionOnSeparateAppDomain,
                    out string className, out string namespaceStringIfAny, out bool hasCodeBehind);
                string baseType = GetCSharpEquivalentOfXamlTypeAsString(_reader.Document.Root.Name, true);

                if (hasCodeBehind)
                {
                    bool isApp = IsClassTheApplicationClass(baseType);

                    string connectMethod = parameters.ComponentConnector.ToString();
                    string initializeComponentMethod = CreateInitializeComponentMethod(
                        $"global::{_settings.Metadata.SystemWindowsNS}.Application",
                        isApp ? $"global::CSHTML5.Internal.StartupAssemblyInfo.OutputResourcesPath = @\"{_outputResourcesPath}\";" : string.Empty,
                        _assemblyNameWithoutExtension,
                        _fileNameWithPathRelativeToProjectRoot,
                        parameters.ResultingFindNameCalls);

                    string additionalConstructors = isApp
                        ? $"private {className}(global::OpenSilver.XamlDesignerConstructorStub stub) {{ InitializeComponent(); }}"
                        : string.Empty;

                    // Wrap everything into a partial class:
                    string partialClass = GeneratePartialClass(additionalConstructors,
                                                               initializeComponentMethod,
                                                               connectMethod,
                                                               parameters.ResultingFieldsForNamedElements,
                                                               className,
                                                               namespaceStringIfAny,
                                                               baseType);

                    string componentTypeFullName = GetFullTypeName(namespaceStringIfAny, className);

                    string factoryClass = GenerateFactoryClass(
                        componentTypeFullName,
                        baseType,
                        GeneratingCode.GetUniqueName(_reader.Document.Root),
                        parameters.CurrentScope.ToString(),
                        $"return ({componentTypeFullName})global::CSHTML5.Internal.TypeInstantiationHelper.Instantiate(typeof({componentTypeFullName}));",
                        parameters.ResultingMethods,
                        $"global::{_settings.Metadata.SystemWindowsNS}.UIElement",
                        _assemblyNameWithoutExtension,
                        _fileNameWithPathRelativeToProjectRoot);

                    string finalCode = $@"
{factoryClass}
{partialClass}";

                    return finalCode;
                }
                else
                {
                    string rootElementName = GeneratingCode.GetUniqueName(_reader.Document.Root);

                    string finalCode = GenerateFactoryClass(
                        baseType,
                        baseType,
                        rootElementName,
                        parameters.CurrentScope.ToString(),
                        string.Join(Environment.NewLine, $"var {rootElementName} = new {baseType}();", $"LoadComponentImpl({rootElementName});", $"return {rootElementName};"),
                        parameters.ResultingMethods,
                        $"global::{_settings.Metadata.SystemWindowsNS}.UIElement",
                        _assemblyNameWithoutExtension,
                        _fileNameWithPathRelativeToProjectRoot);

                    return finalCode;
                }
            }

            private void OnWriteStartObject(GeneratorContext parameters)
            {
                XElement element = _reader.ObjectData.Element;

                // Get information about which element holds the namescope of the current element. For example, if the current element is inside a DataTemplate, the DataTemplate is the root of the namescope of the current element. If the element is not inside a DataTemplate or ControlTemplate, the root of the XAML is the root of the namescope of the current element.
                XElement elementThatIsRootOfTheCurrentNamescope = GetRootOfCurrentNamescopeForRuntime(element);
                bool isElementInRootNamescope = elementThatIsRootOfTheCurrentNamescope.Parent == null;

                // Check if the element is the root element:
                string elementTypeInCSharp = GetCSharpEquivalentOfXamlTypeAsString(
                    element.Name,
                    out string namespaceName,
                    out string localTypeName,
                    out string assemblyNameIfAny);

                bool isRootElement = IsElementTheRootElement(element);
                bool isKnownSystemType = _settings.SystemTypes.IsSupportedSystemType(
                    elementTypeInCSharp.Substring("global::".Length), assemblyNameIfAny
                );
                bool isInitializeTypeFromString =
                    element.Attribute(InsertingImplicitNodes.InitializedFromStringAttribute) != null;

                // Add the constructor (in case of object) or a direct initialization (in case
                // of system type or "isInitializeFromString" or referenced ResourceDictionary)
                // (unless this is the root element)
                string elementUniqueNameOrThisKeyword = GeneratingCode.GetUniqueName(element);

                bool isInNewScope = false;
                GeneratorScope rootScope = parameters.CurrentScope;

                if (isRootElement)
                {
                    parameters.StringBuilder.AppendLine($"_ = {RuntimeHelperClass}.XamlContext_WriteStartObject({parameters.CurrentXamlContext}, {elementUniqueNameOrThisKeyword});");
                }
                else
                {
                    if (isKnownSystemType)
                    {
                        //------------------------------------------------
                        // Add the type initialization from literal value:
                        //------------------------------------------------
                        string directContent;
                        if (element.FirstNode is XText xText)
                        {
                            directContent = xText.Value;
                        }
                        else
                        {
                            // If the direct content is not specified, we use the type's
                            // default value (ex: <sys:String></sys:String>)
                            directContent = _settings.SystemTypes.GetDefaultValue(namespaceName, localTypeName, assemblyNameIfAny);
                        }

                        parameters.StringBuilder.AppendLine(
                            string.Format(
                                "{1} {0} = {3}.XamlContext_WriteStartObject({4}, {2});",
                                elementUniqueNameOrThisKeyword,
                                elementTypeInCSharp,
                                _settings.SystemTypes.ConvertFromInvariantString(directContent, elementTypeInCSharp.Substring("global::".Length)),
                                RuntimeHelperClass,
                                parameters.CurrentXamlContext
                            )
                        );
                    }
                    else if (isInitializeTypeFromString)
                    {
                        //------------------------------------------------
                        // Add the type initialization from string:
                        //------------------------------------------------

                        string stringValue = element.Attribute(InsertingImplicitNodes.InitializedFromStringAttribute).Value;

                        bool isKnownCoreType = _settings.CoreTypes.IsSupportedCoreType(
                            elementTypeInCSharp.Substring("global::".Length), assemblyNameIfAny
                        );

                        string preparedValue = ConvertFromInvariantString(
                            stringValue, elementTypeInCSharp, isKnownCoreType, isKnownSystemType);

                        parameters.StringBuilder.AppendLine(
                            string.Format("var {0} = {2}.XamlContext_WriteStartObject({3}, {1});", 
                                elementUniqueNameOrThisKeyword, 
                                preparedValue,
                                RuntimeHelperClass,
                                parameters.CurrentXamlContext
                            )
                        );
                    }
                    else
                    {
                        if (_settings.Options == XamlPreprocessorOptions.Auto)
                        {
                            isInNewScope = true;

                            var objectScope = new NewObjectScope(elementUniqueNameOrThisKeyword, elementTypeInCSharp);

                            parameters.StringBuilder.AppendLine(
                                $"var {elementUniqueNameOrThisKeyword} = {objectScope.MethodName}({parameters.CurrentXamlContext});");

                            parameters.PushScope(objectScope);
                        }

                        parameters.StringBuilder.AppendLine(
                            $"var {elementUniqueNameOrThisKeyword} = {RuntimeHelperClass}.XamlContext_WriteStartObject({parameters.CurrentXamlContext}, new {elementTypeInCSharp}());");

                        if (IsResourceDictionaryCreatedFromSource(element))
                        {
                            //------------------------------------------------
                            // Add the type initialization from "Source" URI:
                            //------------------------------------------------
                            string absoluteSourceUri = PathsHelper.ConvertToAbsolutePathWithComponentSyntax(
                                element.Attribute("Source").Value,
                                _fileNameWithPathRelativeToProjectRoot,
                                _assemblyNameWithoutExtension);
                            string loadTypeFullName = XamlResourcesHelper.GenerateClassNameFromComponentUri(absoluteSourceUri);

                            parameters.StringBuilder.AppendLine(
                                $"(({IXamlComponentLoaderClass})new {loadTypeFullName}()).LoadComponent({elementUniqueNameOrThisKeyword});");
                        }
                    }
                }

                // Set templated parent if any
                if (rootScope is FrameworkTemplateScope &&
                    _reflectionOnSeparateAppDomain.IsAssignableFrom(_settings.Metadata.SystemWindowsNS, "IFrameworkElement", element.Name.NamespaceName, element.Name.LocalName))
                {
                    parameters.StringBuilder.AppendLine(
                        $"{RuntimeHelperClass}.XamlContext_SetTemplatedParent({parameters.CurrentXamlContext}, {elementUniqueNameOrThisKeyword});");
                }

                if (_reflectionOnSeparateAppDomain.IsAssignableFrom(_settings.Metadata.SystemWindowsMediaAnimationNS, "Timeline", element.Name.NamespaceName, element.Name.LocalName))
                {
                    parameters.StringBuilder.AppendLine($"{RuntimeHelperClass}.XamlContext_SetAnimationContext({parameters.CurrentXamlContext}, {elementUniqueNameOrThisKeyword});");
                }

                if (_reflectionOnSeparateAppDomain.IsAssignableFrom(_settings.Metadata.SystemWindowsNS, "IUIElement", element.Name.NamespaceName, element.Name.LocalName))
                {
                    string xamlPath = element.Attribute(GeneratingPathInXaml.PathInXamlAttribute)?.Value ?? string.Empty;
                    parameters.StringBuilder.AppendLine($"{XamlDesignerBridgeClass}.SetPathInXaml({elementUniqueNameOrThisKeyword}, \"{xamlPath}\");");
                    parameters.StringBuilder.AppendLine($"{XamlDesignerBridgeClass}.SetFilePath({elementUniqueNameOrThisKeyword}, @\"{_sourceFile}\");");
                }

                // Add the attributes:
                foreach (XAttribute attribute in element.Attributes())
                {
                    //-------------
                    // ATTRIBUTE
                    //-------------

                    string attributeValue = GetAttributeValue(attribute);
                    string attributeLocalName = attribute.Name.LocalName;

                    // Skip the utility attributes:
                    if (!IsReservedAttribute(attributeLocalName)
                        && !attribute.IsNamespaceDeclaration)
                    {
                        // Verify that the attribute is not an attached property:
                        //todo: This test does not work 100% of the time. For example if we have <Grid Column="1" ..../> the compiler thinks that Column is a normal property whereas it actually is an attached property.
                        bool isAttachedProperty = attributeLocalName.Contains(".");
                        if (!isAttachedProperty)
                        {
                            bool isXNameAttr = GeneratingCode.IsXNameAttribute(attribute);
                            if (isXNameAttr || GeneratingCode.IsNameAttribute(attribute))
                            {
                                //-------------
                                // x:Name (or "Name")
                                //-------------

                                string name = attributeValue;

                                // Add the code to register the name, etc.
                                if (isElementInRootNamescope && parameters.GenerateFieldsForNamedElements)
                                {
                                    string fieldModifier = _settings.Metadata.FieldModifier;
                                    XAttribute attr = element.Attribute(GeneratingCode.xNamespace + "FieldModifier");
                                    if (attr != null)
                                    {
                                        fieldModifier = (attr.Value ?? "").ToLower();
                                    }

                                    // add '@' to handle cases where x:Name is a forbidden word (for instance 'this'
                                    // or any other c# keyword)
                                    string fieldName = "@" + name;
                                    parameters.ResultingFieldsForNamedElements.Add(string.Format("{0} {1} {2};", fieldModifier, elementTypeInCSharp, fieldName));
                                    parameters.ResultingFindNameCalls.Add($"this.{fieldName} = (({elementTypeInCSharp})(this.FindName(\"{name}\")));");
                                }

                                if (isXNameAttr)
                                {
                                    if (_reflectionOnSeparateAppDomain.IsAssignableFrom(_settings.Metadata.SystemWindowsNS, "DependencyObject",
                                        element.Name.NamespaceName, element.Name.LocalName))
                                    {
                                        parameters.StringBuilder.AppendLine(
                                            $"{elementUniqueNameOrThisKeyword}.SetValue(global::{_settings.Metadata.SystemWindowsNS}.FrameworkElement.NameProperty, \"{name}\");");                                        
                                    }
                                }
                                else
                                {
                                    if (_reflectionOnSeparateAppDomain.DoesTypeContainNameMemberOfTypeString(namespaceName, localTypeName, assemblyNameIfAny))
                                    {
                                        parameters.StringBuilder.AppendLine($"{elementUniqueNameOrThisKeyword}.Name = \"{name}\";");
                                    }
                                }

                                rootScope.RegisterName(name, elementUniqueNameOrThisKeyword);
                            }
                            else if (IsEventTriggerRoutedEventProperty(elementTypeInCSharp, attributeLocalName))
                            {
                                // TODO Check that 'attributeLocalName' is effectively the LoadedEvent routed event.
                                // Silverlight only allows the FrameworkElement.LoadedEvent as value for the EventTrigger.RoutedEvent
                                // property, so for now we assume the xaml is always valid.
                                parameters.StringBuilder.AppendLine($"{elementUniqueNameOrThisKeyword}.RoutedEvent = global::{_settings.Metadata.SystemWindowsNS}.FrameworkElement.LoadedEvent;");
                            }
                            else if (string.IsNullOrEmpty(attribute.Name.NamespaceName) || attribute.Name.NamespaceName == element.Name.NamespaceName)
                            {
                                //-------------
                                // Attributes without namespace
                                //-------------

                                // Verify that there are no markups (they are supposed to have been replaced by XML nodes before entering this method - cf. InsertingMarkupNodesInXaml.InsertMarkupNodes(..)):
                                //if (!attributeValue.StartsWith("{"))
                                if (!InsertingMarkupNodesInXaml.IsMarkupExtension(attribute))
                                {
                                    // Check if the attribute corresponds to a Property, an Event, etc.:
                                    string memberName = attribute.Name.LocalName;
                                    MemberTypes memberType = _reflectionOnSeparateAppDomain.GetMemberType(memberName, namespaceName, localTypeName, assemblyNameIfAny);
                                    switch (memberType)
                                    {
                                        case MemberTypes.Event:

                                            //------------
                                            // C# EVENT
                                            //------------

                                            parameters.StringBuilder.AppendLine(
                                                string.Format("{0}.XamlContext_SetConnectionId({1}, {2}, {3});",
                                                    RuntimeHelperClass,
                                                    parameters.CurrentXamlContext,
                                                    parameters.ComponentConnector.Connect(elementTypeInCSharp, attributeLocalName, attributeValue),
                                                    elementUniqueNameOrThisKeyword)
                                            );

                                            break;
                                        case MemberTypes.Field:
                                        case MemberTypes.Property:

                                            //------------
                                            // C# PROPERTY
                                            //------------

                                            // Generate the code for instantiating the attribute value:
                                            string codeForInstantiatingTheAttributeValue;
                                            if (elementTypeInCSharp == $"global::{_settings.Metadata.SystemWindowsNS}.Setter")
                                            {
                                                //we get the parent Style node (since there is a Style.Setters node that is added, the parent style node is )
                                                if (element.Parent != null && element.Parent.Parent != null && element.Parent.Parent.Name.LocalName == "Style")
                                                {

                                                    if (attributeLocalName == "Property")
                                                    {
                                                        // Style setter property:
                                                        codeForInstantiatingTheAttributeValue = GenerateCodeForSetterProperty(element.Parent.Parent, attributeValue); //todo: support attached properties used in a Setter
                                                    }
                                                    else if (attributeLocalName == "Value")
                                                    {
                                                        var property = element.Attribute("Property");
                                                        if (property != null)
                                                        {
                                                            bool isSetterForAttachedProperty = property.Value.Contains('.');
                                                            XName name = GetCSharpXNameFromTargetTypeOrAttachedPropertyString(element, isSetterForAttachedProperty);
                                                            //string str = GetCSharpFullTypeNameFromTargetTypeString(styleNode, reflectionOnSeparateAppDomain);
                                                            //string[] s = {"::"};
                                                            //string[] splittedStr = str.Split(s, StringSplitOptions.RemoveEmptyEntries);
                                                            //string[] splittedTypeName = splittedStr[splittedStr.Length - 1].Split('.');
                                                            //XName typeName = XName.Get(splittedTypeName[splittedTypeName.Length - 1], splittedStr[0]); 
                                                            string propertyName = isSetterForAttachedProperty ? property.Value.Split('.')[1] : property.Value;
                                                            codeForInstantiatingTheAttributeValue = GenerateCodeForInstantiatingAttributeValue(name,
                                                                propertyName,
                                                                isSetterForAttachedProperty,
                                                                attributeValue,
                                                                element);
                                                        }
                                                        else
                                                            throw new XamlParseException(@"The <Setter> element must declare a ""Property"" attribute.");
                                                    }
                                                    else
                                                        throw new XamlParseException(@"The <Setter> element cannot have attributes other than ""Property"" and ""Value"".");
                                                }
                                                else
                                                    throw new XamlParseException(@"""<Setter/>"" tags can only be declared inside a <Style/>.");
                                            }
                                            else if (elementTypeInCSharp == $"global::{_settings.Metadata.SystemWindowsDataNS}.Binding"
                                                && memberName == "Path")
                                            {
                                                if (TryResolvePathForBinding(attributeValue, element, out string resolvedPath))
                                                {
                                                    parameters.StringBuilder.AppendLine(
                                                        string.Format("{0}.XamlPath = {1};",
                                                            elementUniqueNameOrThisKeyword,
                                                            _settings.SystemTypes.ConvertFromInvariantString(resolvedPath, "System.String")
                                                        )
                                                    );
                                                }

                                                XName typeName = element.Name;
                                                string propertyName = attribute.Name.LocalName;

                                                codeForInstantiatingTheAttributeValue =
                                                    GenerateCodeForInstantiatingAttributeValue(
                                                        typeName,
                                                        propertyName,
                                                        isAttachedProperty,
                                                        attributeValue,
                                                        element
                                                    );
                                            }
                                            else if (elementTypeInCSharp == $"global::{_settings.Metadata.SystemWindowsNS}.TemplateBindingExtension"
                                                && memberName == "Path")
                                            {
                                                ResolvePathForTemplateBinding(attributeValue, element, out string typeName, out string propertyName);
                                                parameters.StringBuilder.AppendLine(
                                                    string.Format("{0}.DependencyPropertyName = {1};",
                                                        elementUniqueNameOrThisKeyword,
                                                        _settings.SystemTypes.ConvertFromInvariantString(propertyName, "System.String")));
                                                if (typeName != null)
                                                {
                                                    parameters.StringBuilder.AppendLine(
                                                        $"{elementUniqueNameOrThisKeyword}.DependencyPropertyOwnerType = typeof({typeName});");
                                                }

                                                codeForInstantiatingTheAttributeValue = null;
                                            }
                                            else
                                            {
                                                //------------
                                                // NORMAL C# PROPERTY
                                                //------------

                                                XName typeName = element.Name;
                                                string propertyName = attribute.Name.LocalName;

                                                codeForInstantiatingTheAttributeValue =
                                                    GenerateCodeForInstantiatingAttributeValue(
                                                        typeName,
                                                        propertyName,
                                                        isAttachedProperty,
                                                        attributeValue,
                                                        element
                                                    );
                                            }

                                            // Append the statement:
                                            if (codeForInstantiatingTheAttributeValue != null)
                                            {
                                                parameters.StringBuilder.AppendLine(
                                                    string.Format(
                                                        "{0}.{1} = {2};",
                                                        elementUniqueNameOrThisKeyword, attributeLocalName, codeForInstantiatingTheAttributeValue
                                                    )
                                                );
                                            }

                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            //-------------
                            // ATTACHED PROPERTY
                            //-------------

                            // Split the attribute name:
                            string[] splitted = attribute.Name.LocalName.Split('.');
                            string classLocalNameForAttachedProperty = splitted[0];
                            XName elementNameForAttachedProperty = attribute.Name.Namespace + classLocalNameForAttachedProperty;
                            string classFullNameForAttachedProperty = GetCSharpEquivalentOfXamlTypeAsString(elementNameForAttachedProperty);
                            string propertyName = splitted[1];

                                // Generate the code for instantiating the attribute value:
                            string codeForInstantiatingTheAttributeValue = GenerateCodeForInstantiatingAttributeValue(
                                elementNameForAttachedProperty,
                                propertyName,
                                isAttachedProperty,
                                attributeValue,
                                element);

                            // Append the statement:
                            parameters.StringBuilder.AppendLine(string.Format("{0}.Set{1}({2},{3});", classFullNameForAttachedProperty, propertyName, elementUniqueNameOrThisKeyword, codeForInstantiatingTheAttributeValue));
                        }
                    }
                }

                if (isInNewScope)
                {
                    parameters.PopScope();
                }
            }

            private void OnWriteEndObject(GeneratorContext parameters)
            {
                parameters.StringBuilder.AppendLine($"{RuntimeHelperClass}.XamlContext_WriteEndObject({parameters.CurrentXamlContext});");
            }

            private void OnWriteStartMember(GeneratorContext parameters)
            {
                XElement element = _reader.MemberData.Target;
                XElement member = _reader.MemberData.Member;

                int idx = member.Name.LocalName.IndexOf('.');
                string typeName = member.Name.LocalName.Substring(0, idx);
                string propertyName = member.Name.LocalName.Substring(idx + 1);

                if (_reflectionOnSeparateAppDomain.IsFrameworkTemplateTemplateProperty(propertyName, member.Name.NamespaceName, typeName))
                {
                    if (member.Elements().Count() > 1)
                    {
                        throw new XamlParseException("A FrameworkTemplate cannot have more than one child.", element);
                    }

                    string frameworkTemplateName = GeneratingCode.GetUniqueName(element);

                    FrameworkTemplateScope scope = new FrameworkTemplateScope(
                        frameworkTemplateName,
                        GeneratingCode.GetUniqueName(member.Elements().First()),
                        _settings.Metadata);

                    parameters.StringBuilder.AppendLine($"{RuntimeHelperClass}.SetTemplateContent({frameworkTemplateName}, {parameters.CurrentXamlContext}, {scope.MethodName});");

                    parameters.PushScope(scope);
                }
            }

            private void OnWriteEndMember(GeneratorContext parameters)
            {
                XElement element = _reader.MemberData.Member;

                // Get the namespace, local name, and optional assembly that correspond to the element:
                GetClrNamespaceAndLocalName(element.Name, out _, out _, out string assemblyNameIfAny);

                // Get information about the parent element (to which the property applies) and the element itself:
                var parentElement = element.Parent;
                string parentElementUniqueNameOrThisKeyword = GeneratingCode.GetUniqueName(parentElement);
                string typeName = element.Name.LocalName.Split('.')[0];
                string propertyName = element.Name.LocalName.Split('.')[1];
                XName elementName = element.Name.Namespace + typeName; // eg. if the element is <VisualStateManager.VisualStateGroups>, this will be "DefaultNamespace+VisualStateManager"

                if (_reflectionOnSeparateAppDomain.IsFrameworkTemplateTemplateProperty(propertyName, element.Name.NamespaceName, typeName))
                {
                    // TODO move call to RuntimeHelpers.SetTemplateContent(...) here
                    parameters.PopScope();
                }
                else
                {
                    XElement child = _reader.MemberData.Value;

                    bool isAttachedProperty = IsPropertyAttached(element);

                    // Check if the property is a collection, in which case we must use ".Add(...)", otherwise a simple "=" is enough:
                    if (IsPropertyOrFieldACollection(element, isAttachedProperty)
                        && (element.Elements().Count() != 1
                        || (!IsTypeAssignableFrom(element.Elements().First().Name, element.Name, isAttached: isAttachedProperty)) // To handle the case where the user explicitly declares the collection element. Example: <Application.Resources><ResourceDictionary><Child x:Key="test"/></ResourceDictionary></Application.Resources> (rather than <Application.Resources><Child x:Key="test"/></Application.Resources>), in which case we need to do "=" instead pf "Add()"
                        && !GeneratingCode.IsBinding(element.Elements().First(), _settings)
                        && element.Elements().First().Name.LocalName != "StaticResourceExtension"
                        && element.Elements().First().Name.LocalName != "StaticResource"
                        && element.Elements().First().Name.LocalName != "TemplateBinding"
                        && element.Elements().First().Name.LocalName != "TemplateBindingExtension"))
                    {
                        //------------------------
                        // PROPERTY TYPE IS A COLLECTION
                        //------------------------

                        string codeToAccessTheEnumerable;
                        if (isAttachedProperty)
                        {
                            string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                                elementName.Namespace.NamespaceName,
                                elementName.LocalName,
                                assemblyNameIfAny);

                            codeToAccessTheEnumerable = string.Format(
                                "{0}.Get{1}({2})",
                                elementTypeInCSharp,
                                propertyName,
                                parentElementUniqueNameOrThisKeyword);
                        }
                        else
                        {
                            codeToAccessTheEnumerable = parentElementUniqueNameOrThisKeyword + "." + propertyName;
                        }

                        if (IsPropertyOrFieldADictionary(element, isAttachedProperty))
                        {
                            string childKey = GetElementXKey(child, out bool isImplicitStyle, out bool isImplicitDataTemplate);
                            if (isImplicitStyle)
                            {
                                parameters.StringBuilder.AppendLine($"((global::System.Collections.IDictionary){codeToAccessTheEnumerable}).Add(typeof({childKey}), {GeneratingCode.GetUniqueName(child)});");
                            }
                            else if (isImplicitDataTemplate)
                            {
                                string key = $"new global::{_settings.Metadata.SystemWindowsNS}.DataTemplateKey(typeof({childKey}))";

                                parameters.StringBuilder.AppendLine($"((global::System.Collections.IDictionary){codeToAccessTheEnumerable}).Add({key}, {GeneratingCode.GetUniqueName(child)});");
                            }
                            else
                            {
                                parameters.StringBuilder.AppendLine($"((global::System.Collections.IDictionary){codeToAccessTheEnumerable}).Add(\"{childKey}\", {GeneratingCode.GetUniqueName(child)});");
                            }
                        }
                        else
                        {
                            parameters.StringBuilder.AppendLine($"((global::System.Collections.IList){codeToAccessTheEnumerable}).Add({GeneratingCode.GetUniqueName(child)});");
                        }
                    }
                    else
                    {
                        //------------------------
                        // PROPERTY TYPE IS NOT A COLLECTION
                        //------------------------

                        string childUniqueName = GeneratingCode.GetUniqueName(child);
                        // Note about "RelativeSource": even though it inherits from "MarkupExtension", we do not was
                        // to consider "RelativeSource" as a markup extension for the compilation because it is only
                        // meant to be used WITHIN another markup extension (sort of a "nested" markup extension),
                        // such as in: "{Binding Background, RelativeSource={RelativeSource Mode=TemplatedParent}}"
                        if (!IsElementAMarkupExtension(child) || (child.Name.LocalName == "RelativeSource"))
                        {
                            if (isAttachedProperty)
                            {
                                string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(elementName.Namespace.NamespaceName, elementName.LocalName, assemblyNameIfAny);
                                parameters.StringBuilder.AppendLine(string.Format("{0}.Set{1}({2}, {3});", elementTypeInCSharp, propertyName, parentElementUniqueNameOrThisKeyword, childUniqueName)); // eg. MyCustomGridClass.SetRow(grid32877267T6, int45628789434);
                            }
                            else
                            {
                                parameters.StringBuilder.AppendLine(string.Format("{0}.{1} = {2};", parentElementUniqueNameOrThisKeyword, propertyName, childUniqueName));
                            }
                        }
                        else
                        {
                            //------------------------------
                            // MARKUP EXTENSIONS:
                            //------------------------------

                            XElement parent = element.Parent;

                            if (child.Name.LocalName == "StaticResource" || child.Name.LocalName == "StaticResourceExtension" || child.Name.LocalName == "ThemeResourceExtension") //todo: see if there are other elements than StaticResource that need the parents //todo: check namespace as well?
                            {
                                //------------------------------
                                // {StaticResource ...}
                                //------------------------------

                                string[] splittedLocalName = element.Name.LocalName.Split('.');
                                string propertyNamespaceName, propertyLocalTypeName;

                                // Attached property
                                if (isAttachedProperty)
                                {
                                    string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                                        element.Name.NamespaceName, splittedLocalName[0], assemblyNameIfAny
                                    );

                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        element.Name.NamespaceName,
                                        splittedLocalName[0],
                                        out propertyNamespaceName,
                                        out propertyLocalTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        isAttached: true
                                    );

                                    parameters.StringBuilder.AppendLine(
                                        string.Format(
                                            "{0}.Set{1}({2}, ({3})({4}.CallProvideValue({5}, {6})));",
                                            elementTypeInCSharp,
                                            propertyName,
                                            GeneratingCode.GetUniqueName(parent),
                                            "global::" + (!string.IsNullOrEmpty(propertyNamespaceName) ? propertyNamespaceName + "." : "") + propertyLocalTypeName,
                                            RuntimeHelperClass,
                                            parameters.CurrentXamlContext,
                                            childUniqueName
                                        )
                                    );
                                }
                                else
                                {
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        parent.Name.Namespace.NamespaceName,
                                        parent.Name.LocalName,
                                        out propertyNamespaceName,
                                        out propertyLocalTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        isAttached: false
                                    );

                                    parameters.StringBuilder.AppendLine(
                                        string.Format(
                                            "{0}.{1} = ({2}){3}.CallProvideValue({4}, {5});",
                                            GeneratingCode.GetUniqueName(parent),
                                            propertyName,
                                            "global::" + (!string.IsNullOrEmpty(propertyNamespaceName) ? propertyNamespaceName + "." : "") + propertyLocalTypeName,
                                            RuntimeHelperClass,
                                            parameters.CurrentXamlContext,
                                            childUniqueName
                                        )
                                    );
                                }
                            }
                            else if (child.Name.LocalName == "Binding" || child.Name.LocalName == "MultiBinding")
                            {
                                //------------------------------
                                // {Binding ...} or MultiBinding
                                //------------------------------

                                bool isDependencyProperty =
                                    _reflectionOnSeparateAppDomain.GetField(
                                        propertyName + "Property",
                                        isAttachedProperty ? elementName.Namespace.NamespaceName : parent.Name.Namespace.NamespaceName,
                                        isAttachedProperty ? elementName.LocalName : parent.Name.LocalName,
                                        _assemblyNameWithoutExtension) != null;

                                string propertyDeclaringTypeName;
                                string propertyTypeNamespace;
                                string propertyTypeName;
                                if (!isAttachedProperty)
                                {
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldInfo(propertyName,
                                                                                         parent.Name.Namespace.NamespaceName,
                                                                                         parent.Name.LocalName,
                                                                                         out propertyDeclaringTypeName,
                                                                                         out propertyTypeNamespace,
                                                                                         out propertyTypeName,
                                                                                         assemblyNameIfAny,
                                                                                         false);
                                }
                                else
                                {
                                    _reflectionOnSeparateAppDomain.GetAttachedPropertyGetMethodInfo("Get" + propertyName,
                                        elementName.Namespace.NamespaceName,
                                        elementName.LocalName,
                                        out propertyDeclaringTypeName,
                                        out propertyTypeNamespace,
                                        out propertyTypeName,
                                        assemblyNameIfAny);
                                }
                                string propertyTypeFullName = (!string.IsNullOrEmpty(propertyTypeNamespace) ? propertyTypeNamespace + "." : "") + propertyTypeName;

                                // Check if the property is of type "Binding/MultiBinding" (or "BindingBase"), in which 
                                // case we should directly assign the value instead of calling "SetBinding"
                                bool isPropertyOfTypeBinding =
                                    propertyTypeFullName == $"global::{_settings.Metadata.SystemWindowsDataNS}.{child.Name.LocalName}" ||
                                    propertyTypeFullName == $"global::{_settings.Metadata.SystemWindowsDataNS}.BindingBase";

                                if (isPropertyOfTypeBinding || !isDependencyProperty)
                                {
                                    parameters.StringBuilder.AppendLine(string.Format("{0}.{1} = {2};", parentElementUniqueNameOrThisKeyword, propertyName, GeneratingCode.GetUniqueName(child)));
                                }
                                else
                                {
                                    parameters.StringBuilder.AppendLine(
                                        string.Format(
                                            "global::{3}.BindingOperations.SetBinding({0}, {1}, {2});",
                                            parentElementUniqueNameOrThisKeyword,
                                            propertyDeclaringTypeName + "." + propertyName + "Property",
                                            GeneratingCode.GetUniqueName(child),
                                            _settings.Metadata.SystemWindowsDataNS)); //we add the container itself since we couldn't add it inside the while
                                }
                            }
                            else if (child.Name.LocalName == "TemplateBindingExtension")
                            {
                                var dependencyPropertyName =
                                    _reflectionOnSeparateAppDomain.GetField(
                                        propertyName + "Property",
                                        isAttachedProperty ? elementName.Namespace.NamespaceName : parent.Name.Namespace.NamespaceName,
                                        isAttachedProperty ? elementName.LocalName : parent.Name.LocalName,
                                        _assemblyNameWithoutExtension);

                                parameters.StringBuilder.AppendLine(string.Format(
                                    "{0}.SetValue({1}, {2}.CallProvideValue({3}, {4}));",
                                    parentElementUniqueNameOrThisKeyword,
                                    dependencyPropertyName,
                                    RuntimeHelperClass,
                                    parameters.CurrentXamlContext,
                                    GeneratingCode.GetUniqueName(child)));
                            }
                            else if (child.Name == GeneratingCode.xNamespace + "NullExtension")
                            {
                                //------------------------------
                                // {x:Null}
                                //------------------------------

                                if (isAttachedProperty)
                                {
                                    string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(elementName.Namespace.NamespaceName, elementName.LocalName, assemblyNameIfAny);
                                    parameters.StringBuilder.AppendLine(string.Format("{0}.Set{1}({2}, null);", elementTypeInCSharp, propertyName, parentElementUniqueNameOrThisKeyword));
                                }
                                else
                                {
                                    parameters.StringBuilder.AppendLine(string.Format("{0}.{1} = null;", parentElementUniqueNameOrThisKeyword, propertyName));
                                }
                                //todo-perfs: avoid generating the line "var NullExtension_cfb65e0262594ddb87d60d8e776ce142 = new global::System.Windows.Markup.NullExtension();", which is never used. Such a line is generated when the user code contains a {x:Null} markup extension.
                            }
                            else if (GeneratingCode.IsStaticExtension(child, _settings))
                            {
                                string staticMemberName = ResolveStaticExtension(child);

                                if (isAttachedProperty)
                                {
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        element.Name.NamespaceName,
                                        typeName,
                                        out string propertyTypeNS,
                                        out string propertyTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        isAttached: true);

                                    string type = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                                        elementName.Namespace.NamespaceName,
                                        elementName.LocalName,
                                        assemblyNameIfAny);

                                    parameters.StringBuilder.AppendLine(
                                        $"{type}.Set{propertyName}({parentElementUniqueNameOrThisKeyword}, ({GetFullTypeName(propertyTypeNS, propertyTypeName)})(object){staticMemberName});");
                                }
                                else
                                {
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        parent.Name.NamespaceName,
                                        parent.Name.LocalName,
                                        out string propertyTypeNS,
                                        out string propertyTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        isAttached: false);

                                    parameters.StringBuilder.AppendLine(
                                        $"{parentElementUniqueNameOrThisKeyword}.{propertyName} = ({GetFullTypeName(propertyTypeNS, propertyTypeName)})(object){staticMemberName};");
                                }
                            }
                            else if (GeneratingCode.IsTypeExtension(child, _settings))
                            {
                                string resolvedTypeName = ResolveTypeExtension(child);

                                if (isAttachedProperty)
                                {
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        element.Name.NamespaceName,
                                        typeName,
                                        out string propertyTypeNS,
                                        out string propertyTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        isAttached: true);

                                    string type = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                                        elementName.Namespace.NamespaceName,
                                        elementName.LocalName,
                                        assemblyNameIfAny);

                                    parameters.StringBuilder.AppendLine(
                                        $"{type}.Set{propertyName}({parentElementUniqueNameOrThisKeyword}, ({GetFullTypeName(propertyTypeNS, propertyTypeName)})(object)typeof(global::{resolvedTypeName}));");
                                }
                                else
                                {
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        parent.Name.NamespaceName,
                                        parent.Name.LocalName,
                                        out string propertyTypeNS,
                                        out string propertyTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        isAttached: false);

                                    parameters.StringBuilder.AppendLine(
                                        $"{parentElementUniqueNameOrThisKeyword}.{propertyName} = ({GetFullTypeName(propertyTypeNS, propertyTypeName)})(object)typeof(global::{resolvedTypeName});");
                                }
                            }
                            else
                            {
                                //------------------------------
                                // Other (custom MarkupExtensions)
                                //------------------------------

                                string propertyKey = GetKeyNameOfProperty(
                                    parent, element.Name.LocalName.Split('.')[1], _reflectionOnSeparateAppDomain
                                );
                                string propertyKeyString = propertyKey ?? "null";

                                if (isAttachedProperty)
                                {
                                    string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                                        elementName.Namespace.NamespaceName, elementName.LocalName, assemblyNameIfAny
                                    );

                                    string[] splittedLocalName = element.Name.LocalName.Split('.');

                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        element.Name.NamespaceName,
                                        splittedLocalName[0],
                                        out string propertyNamespaceName,
                                        out string propertyLocalTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        true
                                    );

                                    string propertyType = string.Format(
                                        "global::{0}{1}{2}",
                                        propertyNamespaceName,
                                        string.IsNullOrEmpty(propertyNamespaceName) ? string.Empty : ".",
                                        propertyLocalTypeName
                                    );

                                    string markupExtension = string.Format(
                                        "(({0}){1}).ProvideValue(new global::System.ServiceProvider({2}, {3}))",
                                        IMarkupExtensionClass, childUniqueName, GeneratingCode.GetUniqueName(parent), propertyKeyString
                                    );

                                    parameters.StringBuilder.AppendLine(
                                        string.Format("{0}.Set{1}({2}, ({3}){4});",
                                                      elementTypeInCSharp,
                                                      propertyName,
                                                      parentElementUniqueNameOrThisKeyword,
                                                      propertyType,
                                                      markupExtension
                                        )
                                    );
                                }
                                else
                                {
                                    // Todo: remove what is irrelevant below:
                                    // Note: the code was copy-pasted from the Binding section from here.
                                    // It is because we need to call SetBinding if a Custom marckup
                                    // expression returns a Binding.
                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldInfo(
                                        propertyName,
                                        parent.Name.Namespace.NamespaceName,
                                        parent.Name.LocalName,
                                        out string propertyDeclaringTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny,
                                        false
                                    );

                                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                                        propertyName,
                                        parent.Name.Namespace.NamespaceName,
                                        parent.Name.LocalName,
                                        out string propertyNamespaceName,
                                        out string propertyLocalTypeName,
                                        out _,
                                        out _,
                                        assemblyNameIfAny
                                    );

                                    string dpName = _reflectionOnSeparateAppDomain.GetField(
                                        propertyName + "Property",
                                        isAttachedProperty ? elementName.Namespace.NamespaceName : parent.Name.Namespace.NamespaceName,
                                        isAttachedProperty ? elementName.LocalName : parent.Name.LocalName,
                                        _assemblyNameWithoutExtension);

                                    if (dpName != null)
                                    {
                                        string markupValue = GeneratingUniqueNames.GenerateUniqueNameFromString("tmp");
                                        string propertyTypeFullName = string.IsNullOrEmpty(propertyNamespaceName) ?
                                            $"global::{propertyLocalTypeName}" :
                                            $"global::{propertyNamespaceName}.{propertyLocalTypeName}";
                                        
                                        parameters.StringBuilder.AppendLine($@"object {markupValue};
if (!{RuntimeHelperClass}.TrySetMarkupExtension({parentElementUniqueNameOrThisKeyword}, {dpName}, {childUniqueName}, out {markupValue}))
{{
    {parentElementUniqueNameOrThisKeyword}.{propertyName} = ({propertyTypeFullName}){markupValue};
}}");
                                    }
                                    else
                                    {
                                        parameters.StringBuilder.AppendLine(
                                            string.Format(
                                                "{0}.{1} = ({2})(({3}){4}).ProvideValue(new global::System.ServiceProvider({0}, {5}));",
                                                GeneratingCode.GetUniqueName(parent),
                                                propertyName,
                                                "global::" + (!string.IsNullOrEmpty(propertyNamespaceName) ? propertyNamespaceName + "." : "") + propertyLocalTypeName,
                                                IMarkupExtensionClass,
                                                childUniqueName,
                                                propertyKeyString));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            private void OnWriteEndMemberCollection(GeneratorContext parameters)
            {
                XElement target = _reader.MemberData.Target;
                XElement child = _reader.MemberData.Value;

                string targetUniqueName = GeneratingCode.GetUniqueName(target);

                if (IsElementADictionary(target))
                {
                    string childKey = GetElementXKey(child, out bool isImplicitStyle, out bool isImplicitDataTemplate);
                    if (isImplicitStyle)
                    {
                        parameters.StringBuilder.AppendLine($"((global::System.Collections.IDictionary){targetUniqueName}).Add(typeof({childKey}), {GeneratingCode.GetUniqueName(child)});");
                    }
                    else if (isImplicitDataTemplate)
                    {
                        string key = $"new global::{_settings.Metadata.SystemWindowsNS}.DataTemplateKey(typeof({childKey}))";

                        parameters.StringBuilder.AppendLine($"((global::System.Collections.IDictionary){targetUniqueName}).Add({key}, {GeneratingCode.GetUniqueName(child)});");
                    }
                    else
                    {
                        parameters.StringBuilder.AppendLine($"((global::System.Collections.IDictionary){targetUniqueName}).Add(\"{childKey}\", {GeneratingCode.GetUniqueName(child)});");
                    }
                }
                else
                {
                    parameters.StringBuilder.AppendLine($"((global::System.Collections.IList){targetUniqueName}).Add({GeneratingCode.GetUniqueName(child)});");
                }
            }

            private void TryCatch(Action<GeneratorContext> method, GeneratorContext parameters)
            {
                try
                {
                    method(parameters);
                }
                catch (XamlParseException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    IXmlLineInfo info = _reader.ObjectData?.Element ?? _reader.MemberData?.Member;

                    throw new XamlParseException(
                        "An unexpected error occurred, see inner exception for more details.",
                        info,
                        ex);
                }
            }

            private XElement GetRootOfCurrentNamescopeForRuntime(XElement element)
            {
                XElement currentElement = element;
                while (currentElement.Parent != null)
                {
                    int index = currentElement.Parent.Name.LocalName.IndexOf(".");
                    if (index > -1)
                    {
                        string namespaceName = currentElement.Parent.Name.NamespaceName;
                        string typeName = currentElement.Parent.Name.LocalName.Substring(0, index);
                        string propertyName = currentElement.Parent.Name.LocalName.Substring(index + 1);

                        if (_reflectionOnSeparateAppDomain.IsFrameworkTemplateTemplateProperty(propertyName, namespaceName, typeName))
                        {
                            return currentElement;
                        }
                    }

                    currentElement = currentElement.Parent;
                }

                return currentElement;
            }

            private bool IsElementTheRootElement(XElement element)
            {
                return (element == _reader.Document.Root);
            }

            private bool IsClassTheApplicationClass(string className)
            {
                return className == $"global::{_settings.Metadata.SystemWindowsNS}.Application";
            }

            private bool IsResourceDictionaryCreatedFromSource(XElement element)
            {
                if (element.Attribute("Source") != null)
                {
                    return _reflectionOnSeparateAppDomain.IsResourceDictionarySourcePropertyVisible(
                        element.Name.NamespaceName, element.Name.LocalName);
                }

                return false;
            }

            private string GenerateCodeForSetterProperty(XElement styleElement, string attributeValue)
            {
                bool isAttachedProperty = attributeValue.Contains(".");
                string elementTypeInCSharp, dependencyPropertyName;
                bool hasNamespace;
                string namespaceName, propertyName;
                // Check for namespace/prefix
                if (attributeValue.Contains(':'))
                {
                    hasNamespace = true;
                    string[] splittedAttributeValue = attributeValue.Split(':');
                    namespaceName = splittedAttributeValue[0];
                    propertyName = splittedAttributeValue[1];
                }
                else
                {
                    hasNamespace = false;
                    namespaceName = "";
                    propertyName = attributeValue;
                }

                if (isAttachedProperty)
                {
                    string[] splittedAttachedProperty = propertyName.Split('.');
                    string propertyFullXamlTypeName = namespaceName + (hasNamespace ? ":" : "") + splittedAttachedProperty[0];
                    GetClrNamespaceAndLocalName(propertyFullXamlTypeName,
                        styleElement,
                        out string elementNamespaceName,
                        out string elementLocalTypeName,
                        out string _);
                    elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                        elementNamespaceName,
                        elementLocalTypeName);

                    dependencyPropertyName = splittedAttachedProperty[1] + "Property";
                }
                else
                {
                    elementTypeInCSharp = GetCSharpFullTypeNameFromTargetTypeString(styleElement);
                    dependencyPropertyName = attributeValue + "Property"; //todo: handle the case where the DependencyProperty name is not the name of the property followed by "Property" (at least improve the error message)
                }
                return string.Format("{0}.{1}", elementTypeInCSharp, dependencyPropertyName);
            }

            private XName GetCSharpXNameFromTargetTypeOrAttachedPropertyString(XElement setterElement, bool isAttachedProperty)
            {
                string namespaceName;
                string localTypeName;
                string assemblyNameIfAny;
                XAttribute attributeToLookAt;
                XElement currentXElement;
                if (isAttachedProperty)
                {
                    currentXElement = setterElement;
                    attributeToLookAt = currentXElement.Attribute("Property");
                    if (attributeToLookAt == null)
                        throw new XamlParseException("Setter must declare a Property.");
                }
                else
                {
                    currentXElement = setterElement.Parent.Parent;
                    attributeToLookAt = currentXElement.Attribute("TargetType");
                    if (attributeToLookAt == null)
                        throw new XamlParseException("Style must declare a TargetType.");
                }

                string attributeTypeString;
                // attribute has a namespace or a prefix
                if (attributeToLookAt.Value.Contains(':'))
                {
                    string[] splittedValue = attributeToLookAt.Value.Split(':');

                    if (isAttachedProperty)
                    {
                        if (splittedValue[1].Contains('.'))
                        {
                            attributeTypeString = splittedValue[0] + ":" + splittedValue[1].Split('.')[0];
                        }
                        else
                        {
                            throw new XamlParseException(@"Namespaces or prefixes must be followed by a type.");
                        }
                    }
                    else
                    {
                        attributeTypeString = attributeToLookAt.Value;
                    }
                }
                else
                {
                    attributeTypeString = attributeToLookAt.Value.Split('.')[0];
                }

                GetClrNamespaceAndLocalName(attributeTypeString, currentXElement, out namespaceName, out localTypeName, out assemblyNameIfAny);
                return _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsXName(namespaceName, localTypeName, assemblyNameIfAny);
            }

            private string GetCSharpFullTypeNameFromTargetTypeString(XElement styleElement, bool isDataType = false)
            {
                var targetTypeAttribute = styleElement.Attribute(isDataType ? "DataType" : "TargetType");
                if (targetTypeAttribute == null)
                    throw new XamlParseException(isDataType ? "DataTemplate must declare a DataType or have a key." : "Style must declare a TargetType.");

                GetClrNamespaceAndLocalName(targetTypeAttribute.Value,
                    styleElement,
                    out string namespaceName,
                    out string localTypeName,
                    out string assemblyNameIfAny);
                string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                    namespaceName,
                    localTypeName,
                    assemblyNameIfAny,
                    ifTypeNotFoundTryGuessing: false);

                return elementTypeInCSharp;
            }

            private string GetCSharpFullTypeName(string typeString, XElement elementWhereTheTypeIsUsed)
            {
                GetClrNamespaceAndLocalName(typeString,
                    elementWhereTheTypeIsUsed,
                    out string namespaceName,
                    out string localTypeName,
                    out string assemblyNameIfAny);
                string elementTypeInCSharp = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                    namespaceName,
                    localTypeName,
                    assemblyNameIfAny,
                    ifTypeNotFoundTryGuessing: false);

                return elementTypeInCSharp;
            }

            private string GetElementXKey(XElement element,
                out bool isImplicitStyle,
                out bool isImplicitDataTemplate)
            {
                isImplicitStyle = false;
                isImplicitDataTemplate = false;

                if (element.Attribute(GeneratingCode.xNamespace + "Key") != null)
                {
                    return element.Attribute(GeneratingCode.xNamespace + "Key").Value;
                }
                else if (element.Attribute(GeneratingCode.xNamespace + "Name") != null)
                {
                    return element.Attribute(GeneratingCode.xNamespace + "Name").Value;
                }
                else if (GeneratingCode.IsStyle(element, _settings))
                {
                    isImplicitStyle = true;
                    return GetCSharpFullTypeNameFromTargetTypeString(element);
                }
                else if (GeneratingCode.IsDataTemplate(element, _settings) && element.Attribute("DataType") != null)
                {
                    isImplicitDataTemplate = true;
                    return GetCSharpFullTypeNameFromTargetTypeString(element, isDataType: true);
                }
                else
                {
                    throw new XamlParseException("Each dictionary entry must have an associated key. The element named '" + element.Name.LocalName + "' does not have a key.");
                }
            }

            private string GenerateCodeForInstantiatingAttributeValue(
                XName xName,
                string propertyName,
                bool isAttachedProperty,
                string value,
                XElement elementWhereTheTypeIsUsed)
            {
                GetClrNamespaceAndLocalName(
                    xName,
                    out string namespaceName,
                    out string localTypeName,
                    out string assemblyNameIfAny);

                string valueNamespaceName, valueLocalTypeName, valueAssemblyName;
                bool isValueEnum;

                if (isAttachedProperty)
                {
                    _reflectionOnSeparateAppDomain.GetMethodReturnValueTypeInfo(
                        "Get" + propertyName,
                        namespaceName,
                        localTypeName,
                        out valueNamespaceName,
                        out valueLocalTypeName,
                        out valueAssemblyName,
                        out isValueEnum,
                        assemblyNameIfAny);
                }
                else
                {
                    _reflectionOnSeparateAppDomain.GetPropertyOrFieldTypeInfo(
                        propertyName,
                        namespaceName,
                        localTypeName,
                        out valueNamespaceName,
                        out valueLocalTypeName,
                        out valueAssemblyName,
                        out isValueEnum,
                        assemblyNameIfAny);
                }

                string valueTypeFullName = string.Format(
                    "global::{0}{1}{2}",
                    valueNamespaceName,
                    string.IsNullOrEmpty(valueNamespaceName) ? string.Empty : ".",
                    valueLocalTypeName
                );

                // Generate the code or instantiating the attribute
                if (isValueEnum)
                {
                    //----------------------------
                    // PROPERTY IS AN ENUM
                    //----------------------------
                    if (value.IndexOf(',') != -1)
                    {
                        string[] split = value.Split(new char[] { ',' });
                        for (int i = 0; i < split.Length; i++)
                        {
                            string fieldName = _reflectionOnSeparateAppDomain.GetEnumValue(
                                split[i].Trim(),
                                valueNamespaceName,
                                valueLocalTypeName,
                                valueAssemblyName,
                                true,
                                false) ?? throw new XamlParseException(
                                    $"Field '{split[i].Trim()}' not found in type: '{valueTypeFullName}'.");
                            
                            split[i] = fieldName;
                        }

                        return string.Join(" | ", split);
                    }
                    else
                    {
                        string fieldName = _reflectionOnSeparateAppDomain.GetEnumValue(
                            value.Trim(),
                            valueNamespaceName,
                            valueLocalTypeName,
                            valueAssemblyName,
                            true,
                            true);

                        return fieldName ?? throw new XamlParseException(
                            $"Field '{value.Trim()}' not found in type: '{valueTypeFullName}'.");
                    }
                }
                else if (valueTypeFullName == "global::System.Type")
                {
                    string typeFullName = GetCSharpFullTypeName(value, elementWhereTheTypeIsUsed);

                    return string.Format("typeof({0})", typeFullName);
                }
                else
                {
                    //----------------------------
                    // PROPERTY IS OF ANOTHER TYPE
                    //----------------------------

                    ChangeRelativePathIntoAbsolutePathIfNecessary(
                        ref value,
                        valueTypeFullName,
                        propertyName,
                        xName);

                    bool isKnownSystemType = _settings.SystemTypes.IsSupportedSystemType(
                        valueTypeFullName.Substring("global::".Length), valueAssemblyName);

                    bool isKnownCoreType = _settings.CoreTypes.IsSupportedCoreType(
                        valueTypeFullName.Substring("global::".Length), valueAssemblyName);

                    if (isAttachedProperty)
                    {
                        return ConvertFromInvariantString(
                            value, valueTypeFullName, isKnownCoreType, isKnownSystemType);
                    }
                    else
                    {
                        string declaringTypeName = _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                            namespaceName, localTypeName, assemblyNameIfAny);

                        return ConvertFromInvariantString(
                            declaringTypeName, propertyName, value, valueTypeFullName, isKnownCoreType, isKnownSystemType);
                    }
                }
            }

            private void ChangeRelativePathIntoAbsolutePathIfNecessary(ref string path,
                string valueTypeFullName,
                string propertyName,
                XName parentXName)
            {
                // In the case of the "Frame" control, a relative URI to a ".xaml" file (used for navigation) should not be changed into an absolute URI, because it is relative to the Startup assembly, not to the current assembly where the value is defined:
                bool IsFrameOrUriMappingSpecialCase =
                    parentXName.LocalName == "UriMapping"
                    || parentXName.LocalName == "Frame"
                    || parentXName.LocalName == "HyperlinkButton";

                // We change relative paths into absolute paths in case of <Image> controls and other controls that have the "Source" property:
                if ((valueTypeFullName == $"global::{_settings.Metadata.SystemWindowsMediaNS}.ImageSource"
                    || valueTypeFullName == "global::System.Uri"
                    || (propertyName == "FontFamily" && path.Contains('.')))
                    && !IsFrameOrUriMappingSpecialCase
                    && !path.ToLower().EndsWith(".xaml")) // Note: this is to avoid messing with Frame controls, which paths are always relative to the startup assembly (in SL).
                {
                    if (!IsUriAbsolute(path) // This lines checks if the URI is in the form "ms-appx://" or "http://" or "https://" or "mailto:..." etc.
                        && !path.ToLower().Contains(@";component/")) // This line checks if the URI is in the form "/assemblyName;component/FolderName/FileName.xaml"
                    {
                        // Get the relative path of the current XAML file:
                        string relativePathOfTheCurrentFile = Path.GetDirectoryName(_fileNameWithPathRelativeToProjectRoot.Replace('\\', '/'));

                        // Combine the relative path of the current file with the path specified by the user:
                        string pathRelativeToProjectRoot = Path.Combine(relativePathOfTheCurrentFile.Replace('\\', '/'), path.Replace('\\', '/')).Replace('\\', '/');

                        // Surround the path with the assembly name to make it an absolute path in the form: "/assemblyName;component/FolderName/FileName.xaml"
                        path = "/" + _assemblyNameWithoutExtension + ";component/" + pathRelativeToProjectRoot;
                    }
                }
            }

            private static bool IsUriAbsolute(string path)
            {
                if (path.Contains(":"))
                {
                    // cf. https://stackoverflow.com/questions/1737575/are-colons-allowed-in-urls
                    string textBeforeColon = path.Substring(0, path.IndexOf(":"));
                    if (!textBeforeColon.Contains(@"\") && !textBeforeColon.Contains(@"/"))
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }

            private bool TryResolvePathForBinding(string path, XElement element, out string resolvedPath)
            {
                if (path == "" || path == ".")
                {
                    resolvedPath = path;
                    return true;
                }

                resolvedPath = null;
                StringBuilder sb = new StringBuilder();

                int pos = 0;
                char c;

                while (true)
                {
                    while (pos < path.Length)
                    {
                        c = path[pos];

                        if (c == '(')
                        {
                            break;
                        }

                        sb.Append(c);
                        pos++;
                    }

                    if (pos == path.Length)
                    {
                        resolvedPath = sb.ToString();
                        return true;
                    }

                    pos++;
                    if (pos == path.Length)
                    {
                        return false;
                    }

                    while (pos < path.Length)
                    {
                        c = path[pos];
                        if (!char.IsWhiteSpace(c))
                        {
                            break;
                        }

                        pos++;
                    }

                    if (pos == path.Length)
                    {
                        return false;
                    }

                    int start = pos;
                    string xmlPrefix = null;
                    string typeName = null;
                    string propertyName = null;
                    while (pos < path.Length)
                    {
                        c = path[pos];
                        if (c == ':')
                        {
                            xmlPrefix = path.Substring(start, pos - start);
                            start = pos + 1;
                        }
                        else if (c == '.')
                        {
                            typeName = path.Substring(start, pos - start);
                            start = pos + 1;
                        }
                        else if (c == ')')
                        {
                            propertyName = path.Substring(start, pos - start);
                            break;
                        }

                        pos++;
                    }

                    if (pos == path.Length)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(propertyName))
                    {
                        return false;
                    }

                    XNamespace xmlNamespace = xmlPrefix == null ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(xmlPrefix);
                    GetClrNamespaceAndLocalName(
                        XName.Get(typeName, xmlNamespace.NamespaceName),
                        out string namespaceName,
                        out string localTypeName,
                        out string assemblyName);

                    string assemblyQualifiedName = _reflectionOnSeparateAppDomain.GetAssemblyQualifiedNameOfXamlType(
                        namespaceName, localTypeName, assemblyName
                    );

                    if (assemblyQualifiedName == null)
                    {
                        return false;
                    }

                    sb.Append('(')
                      .Append(assemblyQualifiedName)
                      .Append('.')
                      .Append(propertyName)
                      .Append(')');

                    pos++;
                }
            }

            private void ResolvePathForTemplateBinding(string path, XElement element, out string typeName, out string propertyName)
            {
                typeName = null;
                propertyName = path;

                int idx1 = path.IndexOf('.');
                if (idx1 > 0 && idx1 < path.Length - 1)
                {
                    string xmlPrefix, type;
                    propertyName = path.Substring(idx1 + 1);

                    int idx2 = path.IndexOf(':', 0, idx1);
                    if (idx2 > -1)
                    {
                        xmlPrefix = path.Substring(0, idx2);
                        type = path.Substring(idx2 + 1, idx1 - idx2 - 1);
                    }
                    else
                    {
                        xmlPrefix = null;
                        type = path.Substring(0, idx1);
                    }

                    XNamespace xmlNamespace = xmlPrefix == null ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(xmlPrefix);
                    try
                    {
                        typeName = GetCSharpEquivalentOfXamlTypeAsString(XName.Get(type, xmlNamespace.NamespaceName));
                    }
                    catch { }
                }
            }

            private string ConvertFromInvariantString(string value, string type, bool isKnownCoreType, bool isKnownSystemType)
            {
                if (_settings.SystemTypes.IsNullableType(type.Substring("global::".Length), null, out string underlyingType))
                {
                    string typeName = underlyingType.Substring("global::".Length);

                    return ConvertFromInvariantStringHelper(value,
                        underlyingType,
                        _settings.CoreTypes.IsSupportedCoreType(typeName, null),
                        _settings.SystemTypes.IsSupportedSystemType(typeName, null),
                        true);
                }
                else
                {
                    return ConvertFromInvariantStringHelper(value, type, isKnownCoreType, isKnownSystemType, false);
                }
            }

            private string ConvertFromInvariantStringHelper(string value, string type, bool isKnownCoreType, bool isKnownSystemType, bool isNullable)
            {
                string preparedValue;

                if (isNullable && string.IsNullOrEmpty(value))
                {
                    preparedValue = "null";
                }
                else if (isKnownCoreType)
                {
                    preparedValue = _settings.CoreTypes.ConvertFromInvariantString(
                        value, type.Substring("global::".Length));
                }
                else if (isKnownSystemType)
                {
                    preparedValue = _settings.SystemTypes.ConvertFromInvariantString(
                        value, type.Substring("global::".Length));
                }
                else
                {
                    preparedValue = CoreTypesHelper.ConvertFromInvariantStringHelper(value, type);
                }

                return preparedValue;
            }

            private string ConvertFromInvariantString(
                string propertyDeclaringType,
                string propertyName,
                string value,
                string propertyType,
                bool isKnownCoreType,
                bool isKnownSystemType)
            {
                return string.Format(
                    "{0}.GetPropertyValue<{1}>(typeof({2}), {3}, {4}, () => {5})",
                    RuntimeHelperClass,
                    propertyType,
                    propertyDeclaringType,
                    EscapeString(propertyName),
                    EscapeString(value),
                    ConvertFromInvariantString(value, propertyType, isKnownCoreType, isKnownSystemType));
            }

            private bool IsEventTriggerRoutedEventProperty(string typeFullName, string propertyName)
                => propertyName == "RoutedEvent" && typeFullName == $"global::{_settings.Metadata.SystemWindowsNS}.EventTrigger";

            private static bool IsReservedAttribute(string attributeName)
            {
                return attributeName == GeneratingUniqueNames.UniqueNameAttribute ||
                       attributeName == InsertingImplicitNodes.InitializedFromStringAttribute ||
                       attributeName == GeneratingPathInXaml.PathInXamlAttribute;
            }

            private static string EscapeString(string stringValue)
            {
                return string.Concat("@\"", stringValue.Replace("\"", "\"\""), "\"");
            }

            private static string GetAttributeValue(XAttribute attribute)
            {
                string value = attribute.Value;

                if (value is not null && value.StartsWith("{}"))
                {
                    return value.Substring(2);
                }

                return value;
            }

            private bool IsPropertyAttached(XElement propertyElement)
            {
                GetClrNamespaceAndLocalName(propertyElement.Name, out string namespaceName, out string localName, out string assemblyNameIfAny);
                if (localName.Contains("."))
                {
                    var split = localName.Split('.');
                    var typeLocalName = split[0];
                    var propertyOrFieldName = split[1];
                    GetClrNamespaceAndLocalName(propertyElement.Parent.Name, out string parentNamespaceName, out string parentLocalTypeName, out string parentAssemblyIfAny);
                    return _reflectionOnSeparateAppDomain.IsPropertyAttached(propertyOrFieldName, namespaceName, typeLocalName, parentNamespaceName, parentLocalTypeName, assemblyNameIfAny);
                }

                return false;
            }

            private bool IsPropertyOrFieldACollection(XElement propertyElement, bool isAttachedProperty)
            {
                if (isAttachedProperty)
                {
                    string methodName = "Get" + propertyElement.Name.LocalName.Split('.')[1]; // In case of attached property, we check the return type of the method "GetPROPERTYNAME()". For example, in case of "Grid.Row", we check the return type of the method "Grid.GetRow()".
                    XName elementName = propertyElement.Name.Namespace + propertyElement.Name.LocalName.Split('.')[0]; // eg. if the propertyElement is <VisualStateManager.VisualStateGroups>, this will be "DefaultNamespace+VisualStateManager"
                    GetClrNamespaceAndLocalName(elementName, out string namespaceName, out string localName, out string assemblyNameIfAny);
                    return _reflectionOnSeparateAppDomain.DoesMethodReturnACollection(methodName, namespaceName, localName, assemblyNameIfAny);
                }
                else
                {
                    var propertyOrFieldName = propertyElement.Name.LocalName.Split('.')[1];
                    var parentElement = propertyElement.Parent;
                    GetClrNamespaceAndLocalName(parentElement.Name, out string parentNamespaceName, out string parentLocalName, out string parentAssemblyNameIfAny);
                    return _reflectionOnSeparateAppDomain.IsPropertyOrFieldACollection(propertyOrFieldName, parentNamespaceName, parentLocalName, parentAssemblyNameIfAny);
                }
            }

            private bool IsPropertyOrFieldADictionary(
                XElement propertyElement,
                bool isAttachedProperty)
            {
                if (isAttachedProperty)
                {
                    string methodName = "Get" + propertyElement.Name.LocalName.Split('.')[1]; // In case of attached property, we check the return type of the method "GetPROPERTYNAME()". For example, in case of "Grid.Row", we check the return type of the method "Grid.GetRow()".
                    XName elementName = propertyElement.Name.Namespace + propertyElement.Name.LocalName.Split('.')[0]; // eg. if the propertyElement is <VisualStateManager.VisualStateGroups>, this will be "DefaultNamespace+VisualStateManager"
                    GetClrNamespaceAndLocalName(elementName, out string namespaceName, out string localName, out string assemblyNameIfAny);
                    return _reflectionOnSeparateAppDomain.DoesMethodReturnADictionary(methodName, namespaceName, localName, assemblyNameIfAny);
                }
                else
                {
                    var propertyOrFieldName = propertyElement.Name.LocalName.Split('.')[1];
                    var parentElement = propertyElement.Parent;
                    GetClrNamespaceAndLocalName(parentElement.Name, out string parentNamespaceName, out string parentLocalName, out string parentAssemblyNameIfAny);
                    return _reflectionOnSeparateAppDomain.IsPropertyOrFieldADictionary(propertyOrFieldName, parentNamespaceName, parentLocalName, parentAssemblyNameIfAny);
                }
            }

            private bool IsElementADictionary(XElement element)
            {
                GetClrNamespaceAndLocalName(element.Name, out string elementNameSpace, out string elementLocalName, out string assemblyNameIfAny);
                return _reflectionOnSeparateAppDomain.IsElementADictionary(elementNameSpace, elementLocalName, assemblyNameIfAny);
            }

            private bool IsElementAMarkupExtension(XElement element)
            {
                GetClrNamespaceAndLocalName(element.Name, out string elementNameSpace, out string elementLocalName, out string assemblyNameIfAny);
                return _reflectionOnSeparateAppDomain.IsElementAMarkupExtension(elementNameSpace, elementLocalName, assemblyNameIfAny);
            }

            private bool IsTypeAssignableFrom(XName elementOfTypeToAssignFrom, XName elementOfTypeToAssignTo, bool isAttached = false)
            {
                GetClrNamespaceAndLocalName(elementOfTypeToAssignFrom, out string nameSpaceOfTypeToAssignFrom, out string nameOfTypeToAssignFrom, out string assemblyNameOfTypeToAssignFrom);
                GetClrNamespaceAndLocalName(elementOfTypeToAssignTo, out string nameSpaceOfTypeToAssignTo, out string nameOfTypeToAssignTo, out string assemblyNameOfTypeToAssignTo);
                return _reflectionOnSeparateAppDomain.IsTypeAssignableFrom(nameSpaceOfTypeToAssignFrom, nameOfTypeToAssignFrom, assemblyNameOfTypeToAssignFrom, nameSpaceOfTypeToAssignTo, nameOfTypeToAssignTo, assemblyNameOfTypeToAssignTo, isAttached);
            }

            private void GetClrNamespaceAndLocalName(
                string typeAsStringInsideAXamlAttribute,
                XElement elementWhereTheTypeIsUsed,
                out string namespaceName,
                out string localName,
                out string assemblyNameIfAny)
            {
                XNamespace xNamespace = null;
                if (typeAsStringInsideAXamlAttribute.Contains(':'))
                {
                    string[] splitted = typeAsStringInsideAXamlAttribute.Split(':');
                    string prefix = splitted[0];
                    typeAsStringInsideAXamlAttribute = splitted[1];
                    xNamespace = elementWhereTheTypeIsUsed.GetNamespaceOfPrefix(prefix);
                }
                if (xNamespace == null)
                {
                    xNamespace = elementWhereTheTypeIsUsed.GetDefaultNamespace();
                }

                XName name = xNamespace + typeAsStringInsideAXamlAttribute;

                GetClrNamespaceAndLocalName(name, out namespaceName, out localName, out assemblyNameIfAny);
            }

            private string GetKeyNameOfProperty(XElement element, string propertyName, AssembliesInspector reflectionOnSeparateAppDomain)
            {
                GetClrNamespaceAndLocalName(element.Name, out string elementNameSpace, out string elementLocalName, out string assemblyNameIfAny);
                return reflectionOnSeparateAppDomain.GetKeyNameOfProperty(elementNameSpace, elementLocalName, assemblyNameIfAny, propertyName);
            }

            private void GetClrNamespaceAndLocalName(XName xName, out string namespaceName, out string localName, out string assemblyNameIfAny)
                => GettingInformationAboutXamlTypes.GetClrNamespaceAndLocalName(
                    xName,
                    _settings.EnableImplicitAssemblyRedirection,
                    out namespaceName,
                    out localName,
                    out assemblyNameIfAny);

            private string GetCSharpEquivalentOfXamlTypeAsString(
                XName xName,
                bool ifTypeNotFoundTryGuessing,
                out string namespaceName,
                out string typeName,
                out string assemblyName)
            {
                GettingInformationAboutXamlTypes.GetClrNamespaceAndLocalName(
                    xName,
                    _settings.EnableImplicitAssemblyRedirection,
                    out namespaceName,
                    out typeName,
                    out assemblyName);

                return _reflectionOnSeparateAppDomain.GetCSharpEquivalentOfXamlTypeAsString(
                    namespaceName,
                    typeName,
                    assemblyName,
                    ifTypeNotFoundTryGuessing);
            }

            private string GetCSharpEquivalentOfXamlTypeAsString(
                XName xName,
                out string namespaceName,
                out string typeName,
                out string assemblyName)
                => GetCSharpEquivalentOfXamlTypeAsString(xName, false, out namespaceName, out typeName, out assemblyName);

            private string GetCSharpEquivalentOfXamlTypeAsString(XName xName, bool ifTypeNotFoundTryGuessing = false)
                => GetCSharpEquivalentOfXamlTypeAsString(xName, ifTypeNotFoundTryGuessing, out _, out _, out _);

            private string ResolveStaticExtension(XElement element)
            {
                if (element.Attribute("Member") is not XAttribute member)
                {
                    throw new XamlParseException("StaticExtension must have Member property set.");
                }

                string fieldString;
                string typeNameForError = null;
                TypeDefinition type;

                if (element.Attribute("MemberType") is XAttribute typeAttribute)
                {
                    type = GetTypeDefinitionFromString(element, typeAttribute.Value);
                    fieldString = member.Value;
                    typeNameForError = type.ConvertToString(SupportedLanguage.CSharp);
                }
                else
                {
                    int dotIndex = member.Value.IndexOf('.');
                    if (dotIndex < 0)
                    {
                        throw new XamlParseException($"'{member.Value}' StaticExtension value cannot be resolved to an enumeration, static field, or static property");
                    }

                    // Pull out the type substring (this will include any XML prefix, e.g. "av:Button")
                    string typeString = member.Value.Substring(0, dotIndex);
                    if (string.IsNullOrEmpty(typeString))
                    {
                        throw new XamlParseException($"'{member.Value}' StaticExtension value cannot be resolved to an enumeration, static field, or static property");
                    }

                    type = GetTypeDefinitionFromString(element, typeString);

                    // Get the member name substring.
                    fieldString = member.Value.Substring(dotIndex + 1, member.Value.Length - dotIndex - 1);
                    if (string.IsNullOrEmpty(typeString))
                    {
                        throw new XamlParseException($"'{member.Value}' StaticExtension value cannot be resolved to an enumeration, static field, or static property");
                    }
                }

                if (type.IsEnum)
                {
                    return _reflectionOnSeparateAppDomain.GetEnumValue(type, fieldString, false, false);
                }

                if (_reflectionOnSeparateAppDomain.GetField(type, fieldString, true, true) is FieldDefinition staticField)
                {
                    return $"global::{staticField.DeclaringType.ConvertToString(SupportedLanguage.CSharp)}.{staticField.Name}";
                }

                if (_reflectionOnSeparateAppDomain.GetProperty(type, fieldString, true, true) is PropertyDefinition staticProperty)
                {
                    return $"global::{staticProperty.DeclaringType.ConvertToString(SupportedLanguage.CSharp)}.{staticProperty.Name}";
                }

                throw new XamlParseException(
                    $"'{(typeNameForError is not null ? $"{typeNameForError}.{member.Value}" : member.Value)}' StaticExtension value cannot be resolved to an enumeration, static field, or static property");
            }

            private string ResolveTypeExtension(XElement element)
            {
                if (element.Attribute("Type") is XAttribute typeAttribute)
                {
                    return GetTypeDefinitionFromString(element, typeAttribute.Value).ConvertToString(SupportedLanguage.CSharp);
                }

                if (element.Attribute("TypeName") is not XAttribute typeNameAttribute)
                {
                    throw new XamlParseException("TypeExtension must have TypeName property set.");
                }

                return GetTypeDefinitionFromString(element, typeNameAttribute.Value).ConvertToString(SupportedLanguage.CSharp);
            }

            private TypeDefinition GetTypeDefinitionFromString(XElement element, string value)
            {
                Debug.Assert(value is not null);

                GetClrNamespaceAndLocalName(value, element, out string namespaceName, out string typeName, out string assemblyName);

                return _reflectionOnSeparateAppDomain.GetTypeDefinition(namespaceName, typeName, assemblyName);
            }
        }
    }
}
