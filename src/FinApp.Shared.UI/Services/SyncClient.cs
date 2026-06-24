using FinApp.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Live-sync client over the server's SignalR hub. Raises <see cref="AccountChanged"/> when a shared
/// account the user belongs to changes elsewhere, and <see cref="InvitationReceived"/> when a new
/// invitation arrives. Handlers fire on a background thread — subscribers must marshal to the UI thread.
/// </summary>
public sealed class SyncClient(ClientOptions options, FinAppApiClient api) : IAsyncDisposable
{
    private HubConnection? _connection;

    public event Action<AccountChangedEvent>? AccountChanged;
    public event Action<InvitationReceivedEvent>? InvitationReceived;

    /// <summary>Fires after the hub auto-reconnects. Group memberships are lost on reconnect and changes during
    /// the outage are missed, so listeners should re-subscribe and refresh (drop any cached state).</summary>
    public event Action? Reconnected;

    /// <summary>True only when the hub is connected — callers gate any "trust the live channel" behaviour on this.</summary>
    public bool IsConnected => _connection is { State: HubConnectionState.Connected };

    public async Task StartAsync()
    {
        if (_connection is not null) return;

        var url = options.BaseUrl.TrimEnd('/') + "/hubs/sync";
        _connection = new HubConnectionBuilder()
            .WithUrl(url, o => o.AccessTokenProvider = () => Task.FromResult(api.Token))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<AccountChangedEvent>(SyncEvents.AccountChanged, e => AccountChanged?.Invoke(e));
        _connection.On<InvitationReceivedEvent>(SyncEvents.InvitationReceived, e => InvitationReceived?.Invoke(e));
        _connection.Reconnected += _ => { Reconnected?.Invoke(); return Task.CompletedTask; };

        await _connection.StartAsync();
    }

    /// <summary>Join an account's live channel after joining it (e.g. just accepted an invite).</summary>
    public async Task SubscribeAsync(Guid accountId)
    {
        if (_connection is { State: HubConnectionState.Connected })
            await _connection.InvokeAsync("Subscribe", accountId);
    }

    public async Task StopAsync()
    {
        if (_connection is null) return;
        await _connection.DisposeAsync();
        _connection = null;
    }

    public ValueTask DisposeAsync() => _connection?.DisposeAsync() ?? ValueTask.CompletedTask;
}
