// See https://aka.ms/new-console-template for more information
using HtmlAgilityPack;
using System.Net;

// Change these two values to change what actually gets downloaded
string volumeId = "vol-9";
int? chaptersToFetch = null; // null == fetch all, numeric value == fetch only that many

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
int chapterIndex = 0;
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
    HtmlDocument chapterDoc = new();
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

    // 4. Remove unecessary or unusuable things
    // 4.1: Like the reader settings button
    RemoveNode(chapterDoc, "//button[contains(@class, 'show-settings-btn')]");
    // 4.2 And the header
    RemoveNode(chapterDoc, "//header[contains(@class, 'site-header')]");
    // 4.3 And the navigation buttons
    RemoveNodes(chapterDoc, "//span[contains(@class, 'nav-previous')]");
    RemoveNodes(chapterDoc, "//span[contains(@class, 'nav-next')]");
    // 4.4 And the announcements section
    RemoveNode(chapterDoc, "//section[@id='announcements']");
    // 4.5 And the comments
    RemoveNode(chapterDoc, "//div[@id='comments']");
    // 4.6 And the misc comment stuff
    RemoveNode(chapterDoc, "//div[@id='wpdiscuz-loading-bar']");
    RemoveNode(chapterDoc, "//div[@id='wpdiscuz-comment-message']");
    // 4.7 And the site footer
    RemoveNode(chapterDoc, "//footer[@id='site-footer']");

    // 5. Inline images as base-64 data images
    HtmlNode contentNode = chapterDoc.GetElementbyId("content");
    HtmlNodeCollection imageNodes = contentNode.SelectNodes("//img");
    if (imageNodes != null)
    {
        IEnumerable<Task> inlineImageTasks = imageNodes.Select(x => InlineImage(httpClient, x));
        await Task.WhenAll(inlineImageTasks);
    }

    // 6. Save the HTML out to disk
    string fileName = $"{chapterIndex + 1:00} - {chapterTitle}.html";
    await File.WriteAllTextAsync(fileName, chapterDoc.DocumentNode.OuterHtml);

    Console.WriteLine($"Wrote out {fileName}.");

    // 7. Check to see if we've found everythiing we need to
    chapterIndex++;
    if (chaptersToFetch.HasValue)
    {
        if (chapterIndex >= chaptersToFetch.Value)
        {
            Console.WriteLine(
                $"Fetched max numbers chapter (max set to {chaptersToFetch.Value}). Ending..."
            );
            break;
        }
    }

    // 8. If there's more to fetch, sleep a bit before getting the next chapter, so we don't get throttled
    Console.WriteLine("Sleeping for 5s...");
    await Task.Delay(5000);
}

void RemoveNode(HtmlDocument doc, string xPathQuery)
{
    HtmlNode? node = doc.DocumentNode.SelectSingleNode(xPathQuery);
    if (node == null)
    {
        Console.WriteLine(
            $"Failed to find and remove node. (Searched with the xpath query '{xPathQuery}')"
        );
        return;
    }

    node.Remove();
}

void RemoveNodes(HtmlDocument doc, string xPathQuery)
{
    HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes(xPathQuery);
    if (nodes == null)
    {
        Console.WriteLine(
            $"Failed to find and remove multiple nodes. (Searched with the xpath query '{xPathQuery}')"
        );
        return;
    }
    foreach (HtmlNode node in nodes)
    {
        node.Remove();
    }
}

async Task InlineImage(HttpClient httpClient, HtmlNode imageNode)
{
    string imgSrc = imageNode.GetAttributeValue("src", "");
    if (imgSrc == "")
    {
        Console.WriteLine("Could not find image source, skipping...");
        return;
    }

    Uri imageUri = new Uri(imgSrc);
    string imageUriNoParams = imageUri.GetLeftPart(UriPartial.Path);
    string? imageExtension = Path.GetExtension(imageUriNoParams);
    if (imageExtension == null)
    {
        Console.WriteLine(
            $"Could not determine file extension of image (looked at: {imgSrc}). Skipping image..."
        );
        return;
    }
    // chop off the leading dot, and lowercase things to be as compliant as possible
    imageExtension = imageExtension[1..].ToLowerInvariant();

    byte[]? imageBytes = null;
    try
    {
        imageBytes = await httpClient.GetByteArrayAsync(imgSrc);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to retrieve image at {imgSrc}: {ex}. Skipping image...");
        return;
    }

    if (imageBytes == null)
    {
        return;
    }

    string base64Image = Convert.ToBase64String(imageBytes);
    imageNode.SetAttributeValue("src", $"data:image/{imageExtension};base64, {base64Image}");
}
