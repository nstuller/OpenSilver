﻿
/*===================================================================================
* 
*   Copyright (c) Userware (OpenSilver.net, CSHTML5.com)
*      
*   This file is part of both the OpenSilver Simulator (https://opensilver.net), which
*   is licensed under the MIT license (https://opensource.org/licenses/MIT), and the
*   CSHTML5 Simulator (http://cshtml5.com), which is dual-licensed (MIT + commercial).
*   
*   As stated in the MIT license, "the above copyright notice and this permission
*   notice shall be included in all copies or substantial portions of the Software."
*  
\*====================================================================================*/

using System.IO;
using System.Reflection;

namespace DotNetForHtml5.EmulatorWithoutJavascript
{
    static class ReflectionInUserAssembliesHelper
    {
        internal static void GetOutputPathsByReadingAssemblyAttributes(out string outputRootPath, out string outputResourcesPath)
        {
            // todo: see if the path can be not hard-coded
            // In the OpenSilver version, the app use the wwwroot folder to store the libs and resources
            // This folder is not inside the build dir (bin\Debug\netstandard2.0) but at the root level
            outputRootPath = @"..\..\..\wwwroot\"; 
            outputResourcesPath = @"\app-cshtml5\res\";
        }
    }
}
