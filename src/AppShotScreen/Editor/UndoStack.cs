using System.Windows;
using System.Windows.Controls;

namespace AppShotScreen.Editor;

public interface IUndoAction
{
    void Undo();
    void Redo();
}

public sealed class AddElementAction(Panel panel, UIElement element) : IUndoAction
{
    public void Undo() => panel.Children.Remove(element);
    public void Redo() { if (!panel.Children.Contains(element)) panel.Children.Add(element); }
}

public sealed class DelegateAction(Action undo, Action redo) : IUndoAction
{
    public void Undo() => undo();
    public void Redo() => redo();
}

public sealed class UndoStack
{
    private readonly Stack<IUndoAction> _undo = new();
    private readonly Stack<IUndoAction> _redo = new();

    public event Action? Changed;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IUndoAction action)
    {
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var a = _undo.Pop();
        a.Undo();
        _redo.Push(a);
        Changed?.Invoke();
    }

    /// <summary>Estado guardado (en el editor con varias capturas, cada una tiene su propio deshacer).</summary>
    public (IUndoAction[] Undo, IUndoAction[] Redo) Save() => (_undo.Reverse().ToArray(), _redo.Reverse().ToArray());

    public void Restore((IUndoAction[] Undo, IUndoAction[] Redo) state)
    {
        _undo.Clear();
        _redo.Clear();
        foreach (var a in state.Undo) _undo.Push(a);
        foreach (var a in state.Redo) _redo.Push(a);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var a = _redo.Pop();
        a.Redo();
        _undo.Push(a);
        Changed?.Invoke();
    }
}
