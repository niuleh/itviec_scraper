type Job =
    { Title: string
      Location: string
      Age: string
      Url: string }

type Company =
    { Name: string
      Size: int
      Country: string
      Location: string
      Rating: float
      NumReviews: int
      RecommendationRate: float
      JobOpenings: Job list }

module Scraper =
    open FSharp.Data
    open FSharp.Data.HttpRequestHeaders
    open FSharpPlus
    open FSharp.Data.Adaptive
    open FSharpx.Collections

    type private CompaniesIndex = HtmlProvider<"https://itviec.com/jobs-company-index">

    let root = "https://itviec.com"

    type private State =
        { CompaniesMap: Map<string, Company>
          Urls: Queue<string>
          Processed: HashSet<string>
          Processing: HashSet<string>
          RateLimit: bool }

    type private ScraperMsg =
        | Url of string
        | Die

    type private Msg =
        | EnqueueUrls of string seq
        | RequestUrl of AsyncReplyChannel<ScraperMsg>
        | Fetched of string * Company
        | NoMatch of string
        | RateLimited of string
        | RequestStatus of AsyncReplyChannel<int * int>
        | Die of AsyncReplyChannel<Map<string, Company>>

    let private Queue () : MailboxProcessor<Msg> =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (oldState: State) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | EnqueueUrls urls ->
                        let newUrls =
                            urls |> Seq.fold (fun (acc: Queue<string>) elem -> acc.Conj(elem)) oldState.Urls

                        return! loop { oldState with Urls = newUrls }

                    | RequestUrl replyChannel when oldState.Urls.IsEmpty ->
                        replyChannel.Reply ScraperMsg.Die
                        return! loop oldState

                    | RequestUrl replyChannel when not oldState.Urls.IsEmpty ->
                        if oldState.RateLimit then
                            System.Threading.Tasks.Task.Delay(3000).Wait()

                        let url, newUrls = oldState.Urls.Uncons

                        let newState =
                            { oldState with
                                Urls = newUrls
                                Processing = oldState.Processing.Add url }

                        replyChannel.Reply(ScraperMsg.Url url)
                        return! loop newState

                    | Fetched(url, company) ->
                        let newState =
                            { oldState with
                                CompaniesMap = oldState.CompaniesMap.Add(url, company)
                                Processed = oldState.Processed.Add url
                                Processing = oldState.Processing.Remove url }

                        return! loop newState

                    | NoMatch url ->
                        let newState =
                            { oldState with
                                Processed = oldState.Processed.Add url
                                Processing = oldState.Processing.Remove url }

                        return! loop newState

                    | RateLimited url when not oldState.RateLimit ->
                        printfn "Rate limit Reached"

                        let newState =
                            { oldState with
                                Urls = oldState.Urls.Conj url
                                Processing = oldState.Processing.Remove url
                                RateLimit = true }

                        return! loop newState

                    | RateLimited url when oldState.RateLimit ->
                        let newState =
                            { oldState with
                                Urls = oldState.Urls.Conj url
                                Processing = oldState.Processing.Remove url }

                        return! loop newState

                    | RequestStatus replyChannel ->
                        replyChannel.Reply(Queue.length oldState.Urls, oldState.Processed.Count)
                        return! loop oldState

                    | Die replyChannel ->
                        replyChannel.Reply oldState.CompaniesMap
                        printfn "Queue: Signal to terminate received. Shutting down."
                        return ()

                    | _ -> failwith "should never happen"
                }

            loop
                { CompaniesMap = Map.empty
                  Urls = Queue.empty
                  Processed = HashSet.Empty
                  Processing = HashSet.Empty
                  RateLimit = false })

    let private (|Number|_|) (input: string) =
        let rx = System.Text.RegularExpressions.Regex("^[0-9]+")
        let _match = rx.Match(input)
        if _match.Success then Some(int _match.Value) else None

    let private PageScraper (name: string) (countries: string list) (q: MailboxProcessor<Msg>) =
        MailboxProcessor.Start(fun _ ->
            let rec loop _ =
                async {
                    let! msg = q.PostAndAsyncReply RequestUrl

                    match msg with
                    | ScraperMsg.Url url ->

                        let response: HttpResponse =
                            Http.Request(
                                url,
                                silentHttpErrors = true,
                                httpMethod = "GET",
                                headers =
                                    [ AcceptLanguage("en-GB,en;q=0.5")
                                      Host("itviec.com")
                                      Accept(HttpContentTypes.Any)
                                      UserAgent("FSharp.Data HTML Type Provider") ]
                            )

                        match response.StatusCode, response.Body with
                        | x, _ when x <> 200 ->
                            q.Post(RateLimited url)
                            return! loop ()

                        // TODO: handle 404

                        | 200, Text body ->

                            let page = HtmlDocument.Parse(body)

                            let companyCountry =
                                page.CssSelect(".col-md-4 span.align-middle").Head.InnerText().Trim()

                            if
                                not (List.contains companyCountry countries)
                                && not (List.contains companyCountry (countries |> List.map (fun s -> s.ToLower())))
                            then
                                q.Post(NoMatch url)
                                return! loop ()
                            else
                                printfn "%s" url

                                let companyName = page.CssSelect("h1.text-md-start").Head.InnerText().Trim()

                                let companyReviews =
                                    page.CssSelect("div.badge-counter")
                                    |> List.tryHead
                                    |> Option.map (fun n -> int (n.InnerText().Trim()))
                                    |> Option.defaultValue 0

                                let companySize =
                                    page.CssSelect(".col-md-4 .normal-text")[1]
                                    |> (fun node ->
                                        match node.InnerText().Trim() with
                                        | Number n -> n
                                        | _ -> failwith "error, no compamny size")

                                let companyLocation =
                                    page.CssSelect("div.igap-2 div.small-text").Head.InnerText().Trim()

                                // TODO: Ratings and RecommendationRate return 0 for every company
                                let companyRating, companyRecommendationRate =
                                    page.CssSelect(".bg-rating-reviews h1.ipe-2")
                                    |> function
                                        | [ a; b ] -> float (a.InnerText()), int (b.InnerText())
                                        | _ -> 0, 0

                                let companyJobs =
                                    try
                                        page.CssSelect(".job-card")
                                        |> List.map (fun jobCard ->
                                            { Title = jobCard.CssSelect(".text-it-black").Head.InnerText().Trim()
                                              Location =
                                                jobCard.CssSelect("span.ips-2.small-text.text-rich-grey")
                                                |> function
                                                    | [ _; a; b ] -> $"{a.InnerText().Trim()}@{b.InnerText().Trim()}"
                                                    | [ a; b ] -> $"{a.InnerText().Trim()}@{b.InnerText().Trim()}"
                                                    | [ a ] -> a.InnerText().Trim()
                                                    | x ->

                                                        printfn "%i - Problem with location %s" x.Length url
                                                        ""
                                              Age =
                                                jobCard
                                                    .CssSelect(
                                                        "span.small-text.text-dark-grey"
                                                    )
                                                    .Head.InnerText()
                                                    .Trim()
                                              Url =
                                                jobCard.CssSelect(".text-it-black a").Head.TryGetAttribute "href"
                                                |> Option.map (fun o -> o.Value())
                                                |> Option.defaultValue "" })
                                    with e ->
                                        printfn "%s: Problem with jobs %s" e.Message url
                                        []

                                q.Post(
                                    Fetched(
                                        url,
                                        { Name = companyName
                                          Size = companySize
                                          Country = companyCountry
                                          Location = companyLocation
                                          Rating = companyRating
                                          NumReviews = companyReviews
                                          RecommendationRate = companyRecommendationRate
                                          JobOpenings = companyJobs }
                                    )
                                )

                                return! loop ()
                    | ScraperMsg.Die ->
                        printfn "PageScraper %s: Signal to terminate received. Shutting down." name
                        return ()
                }

            loop ())

    let private extractLiHrefs (li: HtmlNode) =
        li.CssSelect("a")
        |> Seq.choose (fun a -> a.TryGetAttribute "href" |> Option.map (fun o -> $"{root}{o.Value()}"))

    let Find (countries: string list)(output: string) =
        let page = CompaniesIndex.GetSample()

        let companyPagesA_Z =
            page.Lists.``Jobs by Company``.Html.CssSelect "li"
            |> Seq.map extractLiHrefs
            |> Seq.concat
            |> Seq.toList

        let companyUrls =
            companyPagesA_Z
            |> Seq.map (fun url ->
                HtmlDocument.Load(url).Html().CssSelect ".skill-tag__item"
                |> Seq.map extractLiHrefs
                |> Seq.concat
                |> List.ofSeq)
            |> Seq.concat
            |> Seq.toList

        let queue = Queue()

        queue.Post(EnqueueUrls companyUrls)

        let s1 = PageScraper "s1" countries queue
        let s2 = PageScraper "s2" countries queue
        let s3 = PageScraper "s3" countries queue

        let rec loop (remaining, processed) =
            printfn "remaining: %i, processed: %i" remaining processed

            if processed = companyUrls.Length then
                printfn "Done."
                let m = queue.PostAndReply Die
                System.IO.File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize m)
                printfn "Companies found: %i" m.Count
            else
                System.Threading.Tasks.Task.Delay(10000).Wait()
                loop (queue.PostAndReply RequestStatus)

        loop (queue.PostAndReply RequestStatus)

        printfn "Scraper.companiesTask Done."




module Program =
    open Argu

    type Arguments =
        | [<Mandatory; AltCommandLine("-c"); Unique>] Countries of names: string list
        | [<AltCommandLine("-o"); Unique>] Output of path: string

        interface IArgParserTemplate with
            member this.Usage =
                match this with
                | Countries _ -> "List of countries to search for jobs in"
                | Output _ -> "File to write the results to. Will overwrite if it exists"

    let parser = ArgumentParser.Create<Arguments>(programName = "itviec_scraper")


[<EntryPoint>]
let main args =
    let allArgs = Program.parser.Parse(args)

    let countries = allArgs.GetResult(Program.Arguments.Countries)
    let output = allArgs.GetResult(Program.Arguments.Output)
    Scraper.Find countries output
    0
