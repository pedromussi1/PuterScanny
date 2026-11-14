using CommunityToolkit.Mvvm.Input;
using PuterScanny.App.Controls;
using PuterScanny.Chat;
using PuterScanny.Core.Models;

namespace PuterScanny.App.ViewModels;

/// <summary>The "Ask" tab and the ways other tabs lead into it (and back).</summary>
public sealed partial class MainViewModel
{
    public const string AskTab = "Ask";
    public const string FoldersTab = "Folders";

    private ChatViewModel? _chat;

    public ChatViewModel Chat => _chat ??= CreateChat();

    /// <summary>Asks the window to switch to a results tab ("Ask", "Folders").</summary>
    public event EventHandler<string>? TabRequested;

    private ChatViewModel CreateChat()
    {
        var chat = new ChatViewModel(Ai)
        {
            ShowInFolders = item => { if (item.EntryId is long id) ShowEntryInFolders(id); },
            Reveal = path => RevealFileCommand.Execute(path),
        };
        return chat;
    }

    private void OpenChat()
    {
        if (_ctx is not null) Chat.Open(_ctx, id => AllRows.FirstOrDefault(r => r.EntryId == id));
    }

    /// <summary>Selects an item in the Folders tree (opening the folders above it) and shows the Folders tab.</summary>
    public void ShowEntryInFolders(long entryId)
    {
        if (_ctx is null || RootNodes.FirstOrDefault() is not { } root) return;
        var chain = new List<long>();
        for (var e = _ctx.Db.GetEntry(entryId); e is not null; e = e.ParentId is long p ? _ctx.Db.GetEntry(p) : null)
            chain.Add(e.Id);
        chain.Reverse();

        var node = root;
        foreach (long id in chain.Skip(1))
        {
            node.IsExpanded = true;
            var child = node.Children.FirstOrDefault(c => c.Entry?.Id == id);
            if (child is null) break; // beyond the biggest 500 in its folder: stop at the folder that holds it
            node = child;
        }
        node.IsSelected = true;
        SelectedNode = node;
        TabRequested?.Invoke(this, FoldersTab);
    }

    private bool CanAskAboutSelected() => SelectedNode?.Entry is not null && _ctx is not null;

    /// <summary>"Ask about this": a folder becomes what the questions are about; a file is asked about directly.</summary>
    [RelayCommand(CanExecute = nameof(CanAskAboutSelected))]
    private Task AskAboutSelectedAsync() => AskAboutAsync(SelectedNode?.Entry);

    [RelayCommand]
    private Task AskAboutTreemapSelectedAsync() => AskAboutAsync(TreemapSelected?.Tag as FileSystemEntry);

    private async Task AskAboutAsync(FileSystemEntry? e)
    {
        if (e is null || _ctx is null) return;
        TabRequested?.Invoke(this, AskTab);
        string name = e.Depth == 0 ? e.Path : e.Name;
        if (e.IsDirectory)
        {
            Chat.SetScope(e.Path);
            await Chat.AskWithLookupAsync($"Tell me about {name}", new LookupRequest { Kind = LookupKind.Overview });
        }
        else
        {
            if (System.IO.Path.GetDirectoryName(e.Path) is { } parent) Chat.SetScope(parent);
            await Chat.AskWithLookupAsync($"What is {name}?", new LookupRequest { Kind = LookupKind.Item, Name = e.Path });
        }
    }
}
