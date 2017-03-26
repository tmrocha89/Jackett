using Jackett.Models;
using Jackett.Models.IndexerConfig;
using Jackett.Services;
using Jackett.Utils;
using Jackett.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Text;

namespace Jackett.Indexers
{
    public class YTS : BaseIndexer, IIndexer
    {
        readonly static string defaultSiteLink = "https://yts.ag/";

        private Uri BaseUri
        {
            get { return new Uri(configData.Url.Value); }
            set { configData.Url.Value = value.ToString(); }
        }

        private string ApiEndpoint { get { return BaseUri + "api/v2/list_movies.json"; } }

        new ConfigurationDataUrl configData
        {
            get { return (ConfigurationDataUrl)base.configData; }
            set { base.configData = value; }
        }

        public YTS(IIndexerManagerService i, IWebClient wc, Logger l, IProtectionService ps)
            : base(name: "YTS",
                description: null,
                link: "https://yts.ag/",
                caps: new TorznabCapabilities(),
                manager: i,
                client: wc,
                logger: l,
                p: ps,
                configData: new ConfigurationDataUrl(defaultSiteLink))
        {
            Encoding = Encoding.GetEncoding("windows-1252");
            Language = "en-us";
            Type = "public";

            TorznabCaps.SupportsImdbSearch = true;

            webclient.requestDelay = 2; // 0.5 requests per second

            AddCategoryMapping(3, TorznabCatType.Movies3D, "3D");
            AddCategoryMapping(720, TorznabCatType.MoviesHD, "720p");
            AddCategoryMapping(1080, TorznabCatType.MoviesHD, "1080p");
        }

        public async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            configData.LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Count() > 0, () =>
            {
                throw new Exception("Could not find releases from this URL");
            });

            return IndexerConfigurationStatus.Completed;
        }

        public Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
        {
            return PerformQuery(query, 0);
        }

        private ReleaseInfo getMovieBasicInformation(JToken jsonData)
        {
            var release = new ReleaseInfo();
            release.Title = jsonData.Value<string>("title");
            release.Description = jsonData.Value<string>("description_full");
            release.Comments = new Uri(jsonData.Value<string>("url"));
            string imdbId = jsonData.Value<string>("imdb_code");
            if (!String.IsNullOrEmpty(imdbId))
            {
                release.Imdb = ParseUtil.GetImdbID(imdbId);
            }
            
            ;
            return release;
        }

        private ReleaseInfo getTorrentsInformation(JToken data, ReleaseInfo release)
        {
            release.PublishDate = DateTime.ParseExact(data.Value<string>("date_uploaded"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            release.Seeders = data.Value<int>("seeds");
            release.Peers = data.Value<int>("peers") + release.Seeders;
            release.Size = data.Value<long>("size_bytes");
            release.InfoHash = data.Value<string>("hash");
            release.Link = new Uri(data.Value<string>("url"));
            release.Category = MapTrackerCatDescToNewznab(data.Value<string>("quality"));
            release.DownloadVolumeFactor = 0;
            release.UploadVolumeFactor = 1;
            return release;
        }

        public async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query, int attempts = 0)
        {
            var releases = new List<ReleaseInfo>();
            var searchString = query.GetQueryString();
            
            var queryCollection = new NameValueCollection();
            queryCollection.Add("query_term", query.SearchTerm);

            if (query.ImdbID != null)
            {
                queryCollection.Add("mode", "search");
                queryCollection.Add("search_imdb", query.ImdbID);
            }
            else if (!string.IsNullOrWhiteSpace(searchString))
            {
                queryCollection.Add("mode", "search");
                queryCollection.Add("search_string", searchString);
            }
            else
            {
                queryCollection.Add("mode", "list");
            }

            var cats = string.Join(";", MapTorznabCapsToTrackers(query));
            if (!string.IsNullOrEmpty(cats))
            {
                queryCollection.Add("category", cats);
            }

            var searchUrl = ApiEndpoint + "?" + queryCollection.GetQueryString();
            var response = await RequestStringWithCookiesAndRetry(searchUrl, string.Empty);

            try
            {
                var jsonContent = JObject.Parse(response.Content);

                bool hasError = jsonContent.Value<string>("status") != "ok";
                if (hasError) // no results found
                {
                    return releases.ToArray();
                }

                var jsonData = jsonContent.Value<JObject>("data");

                foreach (var item in jsonData.Value<JArray>("movies"))
                {
                    var release = getMovieBasicInformation(item);

                    var torrentsList = item.Value<JArray>("torrents");
                    foreach(JToken torrentItem in torrentsList)
                    {
                        releases.Add(getTorrentsInformation(torrentItem, (ReleaseInfo)release.Clone()));
                    }
                    
                }
            }
            catch (Exception ex)
            {
                OnParseError(response.Content, ex);
            }

            return releases;
        }

    }
}