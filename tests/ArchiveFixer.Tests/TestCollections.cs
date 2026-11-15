using Xunit;

namespace ArchiveFixer.Tests;

// 串行集合按测试域拆分：每域各自 DisableParallelization，域内串行、与其它集合不并行。
[CollectionDefinition("GlobalState", DisableParallelization = true)]
public sealed class GlobalStateCollection { }

[CollectionDefinition("ExtractionState", DisableParallelization = true)]
public sealed class ExtractionStateCollection { }

[CollectionDefinition("ExtractionRecursionState", DisableParallelization = true)]
public sealed class ExtractionRecursionStateCollection { }

[CollectionDefinition("ViewModelsState", DisableParallelization = true)]
public sealed class ViewModelsStateCollection { }

[CollectionDefinition("InfrastructureState", DisableParallelization = true)]
public sealed class InfrastructureStateCollection { }

[CollectionDefinition("DetectionState", DisableParallelization = true)]
public sealed class DetectionStateCollection { }

[CollectionDefinition("UiState", DisableParallelization = true)]
public sealed class UiStateCollection { }

[CollectionDefinition("BatchState", DisableParallelization = true)]
public sealed class BatchStateCollection { }

[CollectionDefinition("ExtractionState2", DisableParallelization = true)]
public sealed class ExtractionState2Collection { }
