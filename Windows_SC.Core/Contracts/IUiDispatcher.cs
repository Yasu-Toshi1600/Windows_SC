using System;

namespace Windows_SC.Services;

internal interface IUiDispatcher
{
    bool TryEnqueue(Action action);
}
