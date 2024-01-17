## ITViec Scraper

Filters companies and their jobs on [https://itviec.com](https://itviec.com) by country. Totally over-engineered but fun practice with F# MailBoxProcessors, an actor-based concurrency library. 

### Usage

``` pwsh
dotnet run --project .\src\cli\ITViecScraper.fsproj -- -c "$COUNTRY" -o "$PATH_TO_FILE"
```

``` js
// yields:

{
    // {...}
    "https://itviec.com/companies/gft-technologies-vietnam": {
        "Name": "GFT Technologies Vietnam",
        "Size": 151,
        "Country": "Germany",
        "Location": "Ho Chi Minh - Ha Noi",
        "Rating": 0,
        "NumReviews": 53,
        "RecommendationRate": 0,
        "JobOpenings": [
            {
                "Title": "Python Developer",
                "Location": "Hybrid@Ho Chi Minh - Ha Noi",
                "Age": "Posted 2 days ago",
                "Url": "/it-jobs/python-developer-gft-technologies-vietnam-0506?lab_feature=employer_job"
            }
        ]
    },
    // {...}
}
```