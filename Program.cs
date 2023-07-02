// See https://aka.ms/new-console-template for more information
using HtmlAgilityPack;
using System.Net;

string volumeId = "vol-9";

// Get the ToC HTML
string tocUrl = "http://wanderinginn.com/table-of-contents/";
var httpClient = new HttpClient();
var response = await httpClient.GetAsync(tocUrl);
if (!response.IsSuccessStatusCode)
{
    Console.Write($"Failed to do the thing. Details: HTTP {response.StatusCode}");
    return;
}

string htmlResponse = await response.Content.ReadAsStringAsync();
HtmlDocument tocDoc = new HtmlDocument();
tocDoc.LoadHtml(htmlResponse);

// Find the div with the volume header stuff
HtmlNode volumeParentDiv = tocDoc.GetElementbyId(volumeId);

// Get all children that are anchor tags, as those are going to be links to chapters
List<string> chapterUrls = new();
foreach (var childNode in volumeParentDiv.Descendants("a"))
{
    string href = childNode.GetAttributeValue("href", "");
    if (href != "")
    {
        chapterUrls.Add(href);
    }
}

if (!chapterUrls.Any())
{
    Console.WriteLine("Doesn't look like we found any chapters. Bailing.");
    return;
}

// For each discovered URL, download the chapter, and do some processing on it
int chapterNum = 1;
foreach (string chapterUrl in chapterUrls)
{
    Console.WriteLine($"Downloading {chapterUrl}");
    HttpResponseMessage getChapterResponse = await httpClient.GetAsync(chapterUrl);

    if (!getChapterResponse.IsSuccessStatusCode)
    {
        // TODO: Retry logic
        Console.WriteLine(
            $"Failed to download chapter at {getChapterResponse.RequestMessage!.RequestUri}. Reason: {getChapterResponse.StatusCode}, {getChapterResponse.ReasonPhrase}"
        );
        continue;
    }

    string chapterHtml = await getChapterResponse.Content.ReadAsStringAsync();
    HtmlDocument chapterDoc = new HtmlDocument();
    chapterDoc.LoadHtml(chapterHtml);

    // 1. Parse out the chapter title
    HtmlNode titleNode = chapterDoc.DocumentNode.SelectSingleNode(
        "//h1[contains(@class, 'entry-title')]"
    );
    string chapterTitle = WebUtility.HtmlDecode(titleNode.InnerText);

    // 2. Get all the CSS
    HtmlNode cssNode = chapterDoc.DocumentNode.SelectSingleNode(
        "//head//link[contains(@rel, 'stylesheet')]"
    );
    string cssUrl = cssNode.GetAttributeValue("href", "");
    if (cssUrl == "")
    {
        Console.WriteLine(
            $"Unable to retrieve CSS for chapter: {chapterTitle}. Skipping this chapter."
        );
        continue;
    }
    HttpResponseMessage cssResponse = await httpClient.GetAsync(cssUrl);
    if (!cssResponse.IsSuccessStatusCode)
    {
        Console.WriteLine(
            $"Failed to retrieve CSS for chapter {chapterTitle}. Skipping this chapter."
        );
        continue;
    }
    string rawCss = await cssResponse.Content.ReadAsStringAsync();

    // 3. Inline the CSS, and adjust font size stuff
    // Change the default font size to 16pt, because I like it better
    rawCss = rawCss.Replace("--reader-font-size: 12pt;", "--reader-font-size: 16pt;");
    HtmlNode inlineStyleNode = HtmlNode.CreateNode($"<style>{rawCss}</style>");

    // Rip out the JQuery script, because it gets used to INJECT THE READER SIZE INTO THE ROOT HTML TAG
    HtmlNode jqueryScriptNode = chapterDoc.GetElementbyId("jquery-core-js");
    jqueryScriptNode.Remove();

    // Put it next to the existing CSS node
    HtmlNode headNode = chapterDoc.DocumentNode.SelectSingleNode("//head");
    headNode.InsertAfter(inlineStyleNode, cssNode);

    // Remove the old CSS <link> element
    cssNode.Remove();

    // 4. Remove the reader settings button, because it won't work unless we inline the JS, too
    // TODO: remove comments too?
    HtmlNode readerSettingsNode = chapterDoc.DocumentNode.SelectSingleNode(
        "//button[contains(@class, 'show-settings-btn')]"
    );
    readerSettingsNode.Remove();

    // 5. Save the HTML out to disk
    string fileName = $"{chapterNum:00} - {chapterTitle}.html";
    await File.WriteAllTextAsync(fileName, chapterDoc.DocumentNode.OuterHtml);

    Console.WriteLine($"Wrote out {fileName}. Sleeping for 5s...");

    // 6. Sleep a bit before getting the next chapter, so we don't get throttled
    await Task.Delay(5000);
    chapterNum++;
}
