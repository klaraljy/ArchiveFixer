using System.Runtime.CompilerServices;
using System.Windows;

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// 让测试工程能看到 internal 类型（例如 SevenZipListParser、协调器）。
// 这些类型不需要对外公开：它们是实现细节，公开出去只会变成"不能改的 API"。
// 但它们的解析结果直接决定"清理源包"的前置校验，所以必须有单元测试盯着。
[assembly: InternalsVisibleTo("ArchiveFixer.Tests")]
