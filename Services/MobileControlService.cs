using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using FrostLock.Interop;
using FrostLock.Models;

namespace FrostLock.Services;

public sealed record MobileFreezeSnapshot(
    string Phase,
    string? Mode,
    long? RemainingSeconds,
    bool RemoteReleaseAllowed,
    string ObservedAt);

public sealed record MobileLockSettings(
    string Mode,
    int DurationSeconds,
    int ArmSeconds,
    int SafetyMaxSeconds,
    bool BlockAllKeys,
    bool BlockMouse,
    bool BlockSystemKeys,
    string LockScreenStyle,
    bool LockScreenOnAllDisplays,
    bool ShowCountdownOnOverlay,
    bool ShowClockOnOverlay,
    string LockScreenMessage,
    bool KeepDisplayAwake,
    bool PlaySound,
    bool LockWindowsOnRelease,
    bool HasPin,
    bool MobilePinReleaseAllowed);

public sealed record PendingMobilePairing(string DeviceName, string PublicKeyFingerprint);

public sealed record MobilePairingInvite(string Endpoint, string CertificateSha256, string Token, long ExpiresAt, string PcName)
{
    public string ToCode() => MobilePairingCode.Encrypt(new
    {
        v = 1,
        e = Endpoint,
        f = CertificateSha256,
        t = Token,
        x = ExpiresAt,
        n = PcName
    });
}

public sealed class MobileControlService : IDisposable
{
    public const int Port = 44123;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(3);

    // SslStream on Windows cannot use ephemeral private keys ("the platform does not
    // support ephemeral keys"), so the PFX key must live in the user's key store while
    // the listener runs. The PFX itself stays DPAPI-protected in settings.
    private const X509KeyStorageFlags TlsKeyFlags = X509KeyStorageFlags.UserKeySet;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Func<MobileFreezeSnapshot> _getStatus;
    private readonly Func<int, FreezeMode?, string?> _startFreeze;
    private readonly Func<MobileLockSettings> _getLockSettings;
    private readonly Func<MobileLockSettings, string?> _updateLockSettings;
    private readonly Func<bool, string?> _releaseFreeze;
    private readonly Func<PendingMobilePairing, bool> _approvePairing;
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(16, 16);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _idempotencyLock = new();
    private readonly ConcurrentDictionary<string, PairingInviteState> _invites = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _requestIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedResult> _idempotentResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TcpListener> _listeners = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private X509Certificate2? _certificate;
    private volatile string? _lastError;
    private int _challengeRateWindow;
    private int _challengeRateCount;

    public MobileControlService(
        AppSettings settings,
        Dispatcher dispatcher,
        Func<MobileFreezeSnapshot> getStatus,
        Func<int, FreezeMode?, string?> startFreeze,
        Func<MobileLockSettings> getLockSettings,
        Func<MobileLockSettings, string?> updateLockSettings,
        Func<bool, string?> releaseFreeze,
        Func<PendingMobilePairing, bool> approvePairing)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _getStatus = getStatus;
        _startFreeze = startFreeze;
        _getLockSettings = getLockSettings;
        _updateLockSettings = updateLockSettings;
        _releaseFreeze = releaseFreeze;
        _approvePairing = approvePairing;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public bool IsRunning => !_listeners.IsEmpty;
    public string? LastError => _lastError;
    public event EventHandler? NetworkChanged;
    public event EventHandler? PairingCompleted;

    public IReadOnlyList<IPAddress> AvailableAddresses => GetPrivateAddresses();
    public IReadOnlyList<IPAddress> ListeningAddresses => GetPrivateAddresses()
        .Where(address => _listeners.ContainsKey(address.ToString())).ToList();

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_settings.MobileControlEnabled)
            {
                await StopCoreAsync().ConfigureAwait(false);
                _invites.Clear();
                return;
            }

            if (HasActivePublicNetworkProfile() && !_settings.AllowMobileOnPublicProfileForTesting)
            {
                await StopListenersCoreAsync().ConfigureAwait(false);
                _lastError = "Mobile Unlock will not listen while Windows has an active Public network profile. Switch to a trusted Private network, then try again.";
                return;
            }

            var addresses = GetPrivateAddresses();
            if (addresses.Count == 0)
                throw new InvalidOperationException("No private IPv4 network address is available.");

            var desired = addresses.Select(address => address.ToString()).ToHashSet(StringComparer.Ordinal);
            if (_listeners.Count == desired.Count && _listeners.Keys.All(desired.Contains)) return;

            // DHCP, Wi-Fi roaming, VPN changes, and adapter reconnects can all change a
            // local address while FrostLock remains running. Drop stale listeners and
            // refresh them before showing a new invite address.
            await StopListenersCoreAsync().ConfigureAwait(false);
            _lastError = null;

            EnsureCertificate(addresses);

            _stop = new CancellationTokenSource();
            foreach (var address in addresses)
            {
                var listener = new TcpListener(address, Port);
                listener.Start(8);
                if (!_listeners.TryAdd(address.ToString(), listener)) listener.Stop();
                else _ = AcceptLoopAsync(listener, _stop.Token);
            }

            if (_listeners.IsEmpty) throw new InvalidOperationException("No private network interface could be opened.");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            await StopListenersCoreAsync().ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        await StopListenersCoreAsync().ConfigureAwait(false);
    }

    private async Task StopListenersCoreAsync()
    {
        try { _stop?.Cancel(); } catch { }
        _stop?.Dispose();
        _stop = null;
        foreach (var listener in _listeners.Values)
        {
            try { listener.Stop(); } catch { }
        }
        _listeners.Clear();
        _invites.Clear();
        _challenges.Clear();
        _requestIds.Clear();
        _idempotentResults.Clear();
        await Task.CompletedTask;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _ = ReconcileNetworkAsync();
    }

    private async Task ReconcileNetworkAsync()
    {
        try
        {
            // Rebind even when DHCP returns the same address. A listener bound before a
            // Wi-Fi adapter restart can remain stale after the interface comes back.
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_settings.MobileControlEnabled)
                    await StopListenersCoreAsync().ConfigureAwait(false);
            }
            finally { _lifecycleGate.Release(); }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                await StartAsync().ConfigureAwait(false);
                if (!_settings.MobileControlEnabled || IsRunning ||
                    _lastError?.Contains("Public network profile", StringComparison.OrdinalIgnoreCase) == true)
                    break;
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch { /* Network notifications are best-effort; the UI can retry explicitly. */ }
        finally
        {
            try { NetworkChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    public MobilePairingInvite CreateInvite(IPAddress address)
    {
        if (!IsRunning) throw new InvalidOperationException(_lastError ?? "Enable Mobile Unlock before pairing a phone.");
        if (!_listeners.ContainsKey(address.ToString())) throw new InvalidOperationException("That private network address is not listening.");
        CleanupCaches();
        if (_invites.Count >= 8) throw new InvalidOperationException("Close an existing pairing code before creating another one.");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTime.UtcNow.Add(InviteLifetime);
        _invites[token] = new PairingInviteState(expires);
        return new MobilePairingInvite(
            $"https://{address}:{Port}",
            Convert.ToHexString(SHA256.HashData(_certificate!.RawData)).ToLowerInvariant(),
            token,
            new DateTimeOffset(expires).ToUnixTimeSeconds(),
            Environment.MachineName);
    }

    public void RevokeInvite(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _invites.TryRemove(token, out _);
    }

    public IReadOnlyList<PairedMobileDevice> Devices => _settings.MobileDevices.Select(device => device.Clone()).ToList();

    public void RevokeDevice(string deviceId)
    {
        var device = _settings.MobileDevices.FirstOrDefault(value => value.DeviceId == deviceId);
        if (device is null) return;
        _settings.MobileDevices.Remove(device);
        SettingsService.Save(_settings);
    }

    public async Task StopAndDisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        await StopAsync();
        _certificate?.Dispose();
        _certificate = null;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        StopAsync().GetAwaiter().GetResult();
        _certificate?.Dispose();
        _certificate = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { continue; }

            try { await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { client.Dispose(); break; }
            _ = Task.Run(async () =>
            {
                try { await HandleClientAsync(client, cancellationToken).ConfigureAwait(false); }
                finally { _connectionGate.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false
                }, requestTimeout.Token).ConfigureAwait(false);

                var request = await ReadRequestAsync(tls, requestTimeout.Token).ConfigureAwait(false);
                if (request is null) return;
                var response = await RouteAsync(request, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(tls, response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                try
                {
                    await WriteResponseAsync(tls, ApiResponse.Fail("internal_error", "FrostLock could not process the request."), CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                _ = ex;
            }
        }
    }

    private async Task<ApiResponse> RouteAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!_settings.MobileControlEnabled || !IsRunning)
            return ApiResponse.Fail("disabled", "Mobile Unlock is disabled on this PC.");
        if (request.Path.Contains('?') || request.Path.Contains('#')) return ApiResponse.Fail("bad_request", "Unsupported request path.");

        return (request.Method, request.Path) switch
        {
            ("POST", "/v1/pair") => await PairAsync(request.Body, cancellationToken).ConfigureAwait(false),
            ("POST", "/v1/session/challenge") => ChallengeRequest(request.Body),
            ("GET", "/v1/status") => Authenticated(request, () => ApiResponse.Ok(StatusJson(_dispatcher.Invoke(_getStatus)))),
            ("GET", "/v1/lock-settings") => Authenticated(request, () => ApiResponse.Ok(_dispatcher.Invoke(_getLockSettings))),
            ("POST", "/v1/lock-settings") => Authenticated(request, () => UpdateLockSettings(request)),
            ("POST", "/v1/freeze") => Authenticated(request, () => Freeze(request)),
            ("POST", "/v1/unlock") => Authenticated(request, () => Unlock(request)),
            _ => ApiResponse.Fail("not_found", "Route not found.")
        };
    }

    private async Task<ApiResponse> PairAsync(byte[] body, CancellationToken cancellationToken)
    {
        await _pairingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (!root.TryGetProperty("token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String)
            return ApiResponse.Fail("invalid_invite", "Pairing code is invalid or expired.");
        var token = tokenValue.GetString()!;
        if (!_invites.TryRemove(token, out var invite) || invite.ExpiresAt <= DateTime.UtcNow)
            return ApiResponse.Fail("invalid_invite", "Pairing code is invalid or expired.");
        if (_dispatcher.Invoke(() => _settings.MobileDevices.Count(device => !device.Revoked)) >= 32)
            return ApiResponse.Fail("device_limit", "This FrostLock installation already has the maximum number of paired phones.", 409);

        var name = root.TryGetProperty("deviceName", out var nameValue) ? CleanName(nameValue.GetString()) : "Android phone";
        if (!root.TryGetProperty("publicKey", out var keyValue) || keyValue.ValueKind != JsonValueKind.String)
            return ApiResponse.Fail("invalid_key", "Phone identity is invalid.");

        byte[] keyBytes;
        using var publicKey = ECDsa.Create();
        try
        {
            keyBytes = Convert.FromBase64String(keyValue.GetString()!);
            publicKey.ImportSubjectPublicKeyInfo(keyBytes, out var consumed);
            if (consumed != keyBytes.Length || publicKey.KeySize != 256)
                return ApiResponse.Fail("invalid_key", "Phone identity is invalid.");
        }
        catch
        {
            return ApiResponse.Fail("invalid_key", "Phone identity is invalid.");
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        var pending = new PendingMobilePairing(name, fingerprint);
        bool approved;
        try
        {
            approved = await _dispatcher.InvokeAsync(() => _approvePairing(pending)).Task
                .WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ApiResponse.Fail("approval_timeout", "Pairing approval timed out. Try again from FrostLock.");
        }
        catch (OperationCanceledException)
        {
            return ApiResponse.Fail("approval_cancelled", "Pairing was cancelled.");
        }

        if (!approved) return ApiResponse.Fail("pairing_rejected", "Pairing was not approved on the PC.");

        var device = new PairedMobileDevice
        {
            DeviceId = Guid.NewGuid().ToString("N"),
            Name = name,
            PublicKeyBase64 = Convert.ToBase64String(keyBytes),
            PairedAtUtc = DateTime.UtcNow
        };
        await _dispatcher.InvokeAsync(() =>
        {
            _settings.MobileDevices.Add(device);
            SettingsService.Save(_settings);
            PairingCompleted?.Invoke(this, EventArgs.Empty);
        }).Task.ConfigureAwait(false);
        return ApiResponse.Ok(new { deviceId = device.DeviceId });
        }
        finally
        {
            _pairingGate.Release();
        }
    }

    private ApiResponse ChallengeRequest(byte[] body)
    {
        var second = Environment.TickCount64 / 1000;
        var bucket = (int)(second & int.MaxValue);
        if (Volatile.Read(ref _challengeRateWindow) != bucket)
        {
            Interlocked.Exchange(ref _challengeRateCount, 0);
            Interlocked.Exchange(ref _challengeRateWindow, bucket);
        }
        if (Interlocked.Increment(ref _challengeRateCount) > 60)
            return ApiResponse.Fail("rate_limited", "Try again in a moment.");

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("deviceId", out var deviceValue))
            return ApiResponse.Fail("unauthorized", "Device is not paired.", 401);
        var deviceId = deviceValue.GetString();
        if (deviceId is null || !_dispatcher.Invoke(() => _settings.MobileDevices.Any(device => device.DeviceId == deviceId && !device.Revoked)))
            return ApiResponse.Fail("unauthorized", "Device is not paired.", 401);

        CleanupCaches();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTime.UtcNow.AddSeconds(30);
        _challenges[nonce] = new Challenge(deviceId, expiresAt);
        return ApiResponse.Ok(new { nonce, serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), expiresAt = new DateTimeOffset(expiresAt).ToUnixTimeSeconds() });
    }

    private ApiResponse Authenticated(HttpRequest request, Func<ApiResponse> operation)
    {
        if (!request.Headers.TryGetValue("x-frostlock-device", out var deviceId)
            || !request.Headers.TryGetValue("x-frostlock-nonce", out var nonce)
            || !request.Headers.TryGetValue("x-frostlock-timestamp", out var timestampText)
            || !request.Headers.TryGetValue("x-frostlock-request-id", out var requestId)
            || !request.Headers.TryGetValue("x-frostlock-signature", out var signatureText))
            return ApiResponse.Fail("unauthorized", "Request authentication is required.", 401);

        if (!long.TryParse(timestampText, out var timestamp)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 60
            || !Guid.TryParse(requestId, out _))
            return ApiResponse.Fail("unauthorized", "Request authentication failed.", 401);

        if (!_challenges.TryRemove(nonce, out var challenge)
            || challenge.ExpiresAtUtc <= DateTime.UtcNow
            || challenge.DeviceId != deviceId)
            return ApiResponse.Fail("unauthorized", "Request challenge expired or was already used.", 401);

        var device = _dispatcher.Invoke(() => _settings.MobileDevices.FirstOrDefault(value => value.DeviceId == deviceId && !value.Revoked));
        if (device is null) return ApiResponse.Fail("unauthorized", "Device is not paired.", 401);

        byte[] signature;
        byte[] publicKey;
        try
        {
            signature = Convert.FromBase64String(signatureText);
            publicKey = Convert.FromBase64String(device.PublicKeyBase64);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            var bodyHash = Convert.ToHexString(SHA256.HashData(request.Body)).ToLowerInvariant();
            var canonical = Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{nonce}\n{timestamp}\n{requestId}\n{bodyHash}");
            if (!verifier.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return ApiResponse.Fail("unauthorized", "Request signature is invalid.", 401);
        }
        catch
        {
            return ApiResponse.Fail("unauthorized", "Request signature is invalid.", 401);
        }

        if (!_requestIds.TryAdd(requestId, DateTime.UtcNow.AddMinutes(2)))
            return ApiResponse.Fail("replayed_request", "This request was already processed.", 409);
        _dispatcher.Invoke(() =>
        {
            device.LastSeenUtc = DateTime.UtcNow;
            SettingsService.Save(_settings);
        });
        return operation();
    }

    private ApiResponse Freeze(HttpRequest request)
    {
        lock (_idempotencyLock) return FreezeLocked(request);
    }

    private ApiResponse FreezeLocked(HttpRequest request)
    {
        using var json = JsonDocument.Parse(request.Body);
        if (!TryIdempotency(json.RootElement, out var key, out var error)) return ApiResponse.Fail("bad_request", error!);
        var cacheKey = $"{request.Headers["x-frostlock-device"]}:freeze:{key}";
        if (_idempotentResults.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return cached.Response;

        if (!json.RootElement.TryGetProperty("durationSeconds", out var durationValue)
            || !durationValue.TryGetInt32(out var seconds)
            || seconds is < 0 or > 86400
            || seconds is > 0 and < 300)
            return ApiResponse.Fail("invalid_duration", "Choose until unlock or a duration from 5 minutes to 24 hours.", 400);

        FreezeMode? requestedMode = null;
        if (json.RootElement.TryGetProperty("mode", out var modeValue))
        {
            if (modeValue.ValueKind != JsonValueKind.String
                || !Enum.TryParse(modeValue.GetString(), true, out FreezeMode parsedMode)
                || parsedMode is not (FreezeMode.Gentle or FreezeMode.Strict or FreezeMode.Pin))
                return ApiResponse.Fail("invalid_mode", "Phones may select Gentle, Strict, or configured PIN mode. Hard and USB Key are PC-only.", 400);
            requestedMode = parsedMode;
        }

        var (errorMessage, currentStatus) = _dispatcher.Invoke(() =>
        {
            var mode = requestedMode ?? _settings.Mode;
            if (mode is not (FreezeMode.Gentle or FreezeMode.Strict or FreezeMode.Pin))
                return ("Remote freeze is available only in Gentle, Strict, or configured PIN mode.", _getStatus());
            if (mode == FreezeMode.Pin && !_settings.HasPin)
                return ("Set a PIN on the PC before selecting PIN mode.", _getStatus());
            var status = _getStatus();
            if (status.Phase != "idle") return ("A freeze is already active.", status);
            var error = _startFreeze(seconds, requestedMode);
            return (error, _getStatus());
        });
        var response = errorMessage is null
            ? ApiResponse.Ok(StatusJson(currentStatus))
            : ApiResponse.Fail(errorMessage.Contains("already active", StringComparison.OrdinalIgnoreCase)
                ? "freeze_already_active" : "freeze_rejected", errorMessage, 409, StatusJson(currentStatus));
        _idempotentResults[cacheKey] = new CachedResult(response, DateTime.UtcNow.AddMinutes(10));
        return response;
    }

    private ApiResponse UpdateLockSettings(HttpRequest request)
    {
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            var mode = RequiredString(root, "mode");
            var style = RequiredString(root, "lockScreenStyle");
            if (!Enum.TryParse<FreezeMode>(mode, true, out var parsedMode)
                || parsedMode is not (FreezeMode.Gentle or FreezeMode.Strict or FreezeMode.Pin))
                return ApiResponse.Fail("invalid_mode", "Phones may select Gentle, Strict, or PIN mode. Hard and USB Key are controlled on the PC.", 400);
            if (!Enum.TryParse<LockScreenStyle>(style, true, out var parsedStyle))
                return ApiResponse.Fail("invalid_lock_screen_style", "Choose Solid, Dim, or None for the lock screen.", 400);

            var settings = new MobileLockSettings(
                parsedMode.ToString(),
                RequiredInt(root, "durationSeconds"),
                RequiredInt(root, "armSeconds"),
                RequiredInt(root, "safetyMaxSeconds"),
                RequiredBool(root, "blockAllKeys"),
                RequiredBool(root, "blockMouse"),
                RequiredBool(root, "blockSystemKeys"),
                parsedStyle.ToString(),
                RequiredBool(root, "lockScreenOnAllDisplays"),
                RequiredBool(root, "showCountdownOnOverlay"),
                RequiredBool(root, "showClockOnOverlay"),
                RequiredString(root, "lockScreenMessage"),
                RequiredBool(root, "keepDisplayAwake"),
                RequiredBool(root, "playSound"),
                RequiredBool(root, "lockWindowsOnRelease"),
                false,
                false);

            if (settings.DurationSeconds is < 0 or > 86400 || settings.DurationSeconds is > 0 and < 300)
                return ApiResponse.Fail("invalid_duration", "Choose until unlock or a duration from 5 minutes to 24 hours.", 400);
            if (settings.ArmSeconds is < 0 or > 120 || settings.SafetyMaxSeconds is < 0 or > 86400)
                return ApiResponse.Fail("invalid_safety_setting", "Arm delay must be 0 to 120 seconds and safety auto-release 0 to 1440 minutes.", 400);
            if (settings.LockScreenMessage.Length > 120)
                return ApiResponse.Fail("invalid_message", "Lock screen message must be 120 characters or fewer.", 400);

            var error = _dispatcher.Invoke(() => _updateLockSettings(settings));
            return error is null
                ? ApiResponse.Ok(_dispatcher.Invoke(_getLockSettings))
                : ApiResponse.Fail("settings_rejected", error, 409);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return ApiResponse.Fail("bad_request", "Lock settings are incomplete or invalid.", 400);
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new InvalidOperationException();

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidOperationException();

    private static bool RequiredBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidOperationException();

    private ApiResponse Unlock(HttpRequest request)
    {
        lock (_idempotencyLock) return UnlockLocked(request);
    }

    private ApiResponse UnlockLocked(HttpRequest request)
    {
        using var json = JsonDocument.Parse(request.Body);
        if (!TryIdempotency(json.RootElement, out var key, out var error)) return ApiResponse.Fail("bad_request", error!);
        var cacheKey = $"{request.Headers["x-frostlock-device"]}:unlock:{key}";
        if (_idempotentResults.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return cached.Response;

        var (releaseError, snapshot) = _dispatcher.Invoke(() =>
        {
            var status = _getStatus();
            if (!status.RemoteReleaseAllowed)
                return ("The active mode does not allow release from a phone.", status);
            var error = _releaseFreeze(_settings.MobilePinReleaseAllowed);
            return (error, _getStatus());
        });
        var response = releaseError is null && snapshot.Phase == "idle"
            ? ApiResponse.Ok(StatusJson(snapshot))
            : ApiResponse.Fail("remote_release_denied", releaseError ?? "The freeze is still active.", 403, StatusJson(snapshot));
        _idempotentResults[cacheKey] = new CachedResult(response, DateTime.UtcNow.AddMinutes(10));
        return response;
    }

    private static MobileFreezeSnapshot StatusJson(MobileFreezeSnapshot snapshot) => snapshot;

    private static bool TryIdempotency(JsonElement root, out string key, out string? error)
    {
        key = string.Empty;
        error = null;
        if (!root.TryGetProperty("idempotencyKey", out var value) || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var parsed))
        {
            error = "A valid idempotency key is required.";
            return false;
        }
        key = parsed.ToString("N");
        return true;
    }

    private static string CleanName(string? name)
    {
        var clean = new string((name ?? "Android phone").Where(character => !char.IsControl(character)).Take(40).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Android phone" : clean;
    }

    private async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(1024);
        var one = new byte[1];
        while (header.Count < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0) return null;
            header.Add(one[0]);
            var count = header.Count;
            if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10)
                break;
        }
        if (header.Count >= MaxHeaderBytes) throw new InvalidDataException("Request headers are too large.");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length != 3 || first[2] != "HTTP/1.1") throw new InvalidDataException("Unsupported HTTP request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(value => value.Length > 0))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("Invalid HTTP header.");
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (!headers.TryAdd(name, value)) throw new InvalidDataException("Duplicate HTTP header.");
        }
        if (headers.ContainsKey("transfer-encoding")) throw new InvalidDataException("Transfer encoding is not supported.");
        var bodyLength = 0;
        if (headers.TryGetValue("content-length", out var lengthText)
            && (!int.TryParse(lengthText, out bodyLength) || bodyLength < 0 || bodyLength > MaxBodyBytes))
            throw new InvalidDataException("Request body is too large.");
        if (first[0] != "GET" && !headers.ContainsKey("content-length")) throw new InvalidDataException("Content-Length is required.");
        if (first[0] == "POST"
            && (!headers.TryGetValue("content-type", out var contentType)
                || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Only JSON requests are supported.");
        var body = new byte[bodyLength];
        if (bodyLength > 0) await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return new HttpRequest(first[0], first[1], headers, body);
    }

    private static async Task WriteResponseAsync(Stream stream, ApiResponse response, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId = Guid.NewGuid().ToString("N"),
            ok = response.Error is null,
            error = response.Error,
            data = response.Data
        }, JsonOptions);
        var reason = response.StatusCode switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 409 => "Conflict", 429 => "Too Many Requests", _ => "Internal Server Error"
        };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.StatusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureCertificate(IReadOnlyList<IPAddress> addresses)
    {
        if (!string.IsNullOrWhiteSpace(_settings.MobileServerCertificatePfx))
        {
            try
            {
                var pfx = Convert.FromBase64String(_settings.MobileServerCertificatePfx);
                _certificate = X509CertificateLoader.LoadPkcs12(pfx, (string?)null, TlsKeyFlags);
                CryptographicOperations.ZeroMemory(pfx);
            }
            catch { _certificate?.Dispose(); _certificate = null; }
        }
        if (_certificate is null) RotateCertificate(addresses);
    }

    private void RotateCertificate(IReadOnlyList<IPAddress> addresses)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={Environment.MachineName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var address in addresses) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = generated.Export(X509ContentType.Pkcs12);
        _certificate?.Dispose();
        _certificate = X509CertificateLoader.LoadPkcs12(pfx, (string?)null, TlsKeyFlags);
        _settings.MobileServerCertificatePfx = Convert.ToBase64String(pfx);
        CryptographicOperations.ZeroMemory(pfx);
        _settings.MobileDevices.Clear();
        SettingsService.Save(_settings);
    }

    private static HashSet<IPAddress> ReadCertificateAddresses(X509Certificate2 certificate)
    {
        var result = new HashSet<IPAddress>();
        var extension = certificate.Extensions.Cast<X509Extension>().FirstOrDefault(item => item.Oid?.Value == "2.5.29.17");
        if (extension is null) return result;
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (reader.HasData)
            {
                var tag = reader.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 7)
                {
                    var address = reader.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                    if (address.Length == 4) result.Add(new IPAddress(address));
                }
                else reader.ReadEncodedValue();
            }
        }
        catch { result.Clear(); }
        return result;
    }

    /// <summary>
    /// Private IPv4 addresses, with the address most likely reachable from a phone first.
    /// Virtual adapters (Hyper-V, WSL, VPN and container bridges) usually have no default
    /// gateway and must not be offered as the default pairing endpoint.
    /// </summary>
    private static List<IPAddress> GetPrivateAddresses()
    {
        var candidates = new List<(IPAddress Address, int Rank)>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                && !gateway.Address.Equals(IPAddress.Any));
            var virtualAdapter = LooksVirtual(adapter);
            var physical = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
            var rank = (virtualAdapter ? 4 : 0) + (hasGateway ? 0 : 2) + (physical ? -1 : 0);

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!IsPrivateIpv4(unicast.Address)) continue;
                candidates.Add((unicast.Address, rank));
            }
        }

        return candidates
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .GroupBy(item => item.Address)
            .Select(group => group.First().Address)
            .ToList();
    }

    private static bool LooksVirtual(NetworkInterface adapter)
    {
        var description = $"{adapter.Name} {adapter.Description}";
        string[] markers =
        [
            "vethernet", "hyper-v", "virtual", "vmware", "virtualbox", "docker",
            "wsl", "loopback", "tap-", "tun", "vpn", "tailscale", "zerotier",
            "hamachi", "radmin", "bluetooth", "pseudo", "container", "default switch"
        ];
        return markers.Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
               || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool HasActivePublicNetworkProfile()
    {
        object? policy = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            policy = Activator.CreateInstance(type)!;
            dynamic firewallPolicy = policy;
            int activeProfiles = firewallPolicy.CurrentProfileTypes;
            return (activeProfiles & 4) != 0; // NET_FW_PROFILE2_PUBLIC
        }
        catch
        {
            // Fail closed if Windows cannot report the active firewall profile.
            return true;
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
        }
    }

    private void CleanupCaches()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _challenges.Where(item => item.Value.ExpiresAtUtc <= now).ToArray()) _challenges.TryRemove(pair.Key, out _);
        foreach (var pair in _requestIds.Where(item => item.Value <= now).ToArray()) _requestIds.TryRemove(pair.Key, out _);
        foreach (var pair in _idempotentResults.Where(item => item.Value.ExpiresAtUtc <= now).ToArray()) _idempotentResults.TryRemove(pair.Key, out _);
        foreach (var pair in _invites.Where(item => item.Value.ExpiresAt <= now).ToArray()) _invites.TryRemove(pair.Key, out _);
    }

    private sealed record PairingInviteState(DateTime ExpiresAt);
    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
    private sealed record Challenge(string DeviceId, DateTime ExpiresAtUtc);
    private sealed record CachedResult(ApiResponse Response, DateTime ExpiresAtUtc);

    private sealed record ApiResponse(int StatusCode, object? Data, ApiError? Error)
    {
        public static ApiResponse Ok(object? data) => new(200, data, null);
        public static ApiResponse Fail(string code, string message, int status = 400, object? data = null) => new(status, data, new ApiError(code, message));
    }

    private sealed record ApiError(string Code, string Message);
}
