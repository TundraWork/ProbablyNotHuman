// Creates or updates a Steam Workshop item through the Steam client that is already running and signed in,
// so no password or Steam Guard code is needed. Driven by scripts/publish_workshop.py --steam-client.
//
//   dotnet run --project tools/WorkshopUpload -- <request.json>
//
// request.json: appid, publishedfileid ("0" creates the item), contentfolder, previewfile, visibility (0 public,
// 1 friends, 2 hidden), title, description, changenote. Prints key=value lines; "publishedfileid=<id>" comes out
// as soon as the item exists, before the content upload.
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Steamworks;

static class Program
{
    static T Wait<T>(SteamAPICall_t call, Action tick = null, int timeoutSeconds = 900) where T : struct
    {
        T value = default;
        bool done = false, failed = false;
        using var result = CallResult<T>.Create((answer, ioFailure) => { value = answer; failed = ioFailure; done = true; });
        result.Set(call);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!done)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Steam did not answer in time");
            SteamAPI.RunCallbacks();
            tick?.Invoke();
            Thread.Sleep(100);
        }
        if (failed) throw new IOException("Steam reported an I/O failure");
        return value;
    }

    static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: WorkshopUpload <request.json>");
            return 2;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
        var request = document.RootElement;
        string Text(string name) => request.GetProperty(name).GetString();
        var appId = new AppId_t(uint.Parse(Text("appid")));
        var fileId = new PublishedFileId_t(ulong.Parse(Text("publishedfileid")));
        foreach (var path in new[] { Text("contentfolder"), Text("previewfile") })
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Console.Error.WriteLine($"Missing {path}");
                return 2;
            }

        Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());
        if (!SteamAPI.Init())
        {
            Console.Error.WriteLine("Could not reach Steam. Start the Steam client and sign in with an account that owns the game.");
            return 1;
        }
        try
        {
            Console.WriteLine($"steam_user={SteamFriends.GetPersonaName()}");
            if (fileId.m_PublishedFileId == 0)
            {
                var created = Wait<CreateItemResult_t>(SteamUGC.CreateItem(appId, EWorkshopFileType.k_EWorkshopFileTypeCommunity));
                if (created.m_eResult != EResult.k_EResultOK)
                {
                    Console.Error.WriteLine($"CreateItem failed: {created.m_eResult}");
                    return 1;
                }
                fileId = created.m_nPublishedFileId;
                Console.WriteLine($"publishedfileid={fileId.m_PublishedFileId}");
                Console.WriteLine($"needs_legal_agreement={created.m_bUserNeedsToAcceptWorkshopLegalAgreement.ToString().ToLowerInvariant()}");
            }

            var update = SteamUGC.StartItemUpdate(appId, fileId);
            SteamUGC.SetItemTitle(update, Text("title"));
            SteamUGC.SetItemDescription(update, Text("description"));
            SteamUGC.SetItemContent(update, Path.GetFullPath(Text("contentfolder")));
            SteamUGC.SetItemPreview(update, Path.GetFullPath(Text("previewfile")));
            SteamUGC.SetItemVisibility(update, (ERemoteStoragePublishedFileVisibility)int.Parse(Text("visibility")));

            var lastStatus = EItemUpdateStatus.k_EItemUpdateStatusInvalid;
            var submitted = Wait<SubmitItemUpdateResult_t>(SteamUGC.SubmitItemUpdate(update, Text("changenote")), () =>
            {
                var status = SteamUGC.GetItemUpdateProgress(update, out _, out _);
                if (status != lastStatus && status != EItemUpdateStatus.k_EItemUpdateStatusInvalid)
                    Console.WriteLine($"status={status}");
                lastStatus = status;
            });
            if (submitted.m_eResult != EResult.k_EResultOK)
            {
                Console.Error.WriteLine($"SubmitItemUpdate failed: {submitted.m_eResult}");
                return 1;
            }
            if (submitted.m_bUserNeedsToAcceptWorkshopLegalAgreement)
                Console.WriteLine("needs_legal_agreement=true");
            Console.WriteLine($"publishedfileid={fileId.m_PublishedFileId}");
            Console.WriteLine("result=ok");
            return 0;
        }
        finally
        {
            SteamAPI.Shutdown();
        }
    }
}
