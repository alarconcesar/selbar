using System.Runtime.InteropServices;

// Todos los P/Invoke de la app apuntan a DLL del sistema (user32, kernel32,
// dwmapi, shcore). Restringir la búsqueda a System32 impide que una DLL
// plantada junto al .exe, en el directorio de trabajo o en una ruta del PATH
// secuestre la carga (CA5392). El atributo solo es válido a nivel de ensamblado
// o de método, no de clase.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
