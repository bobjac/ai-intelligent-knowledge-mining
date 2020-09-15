using System;
using Microsoft.Azure.Search;
using Microsoft.Azure.Search.Models;
using Microsoft.Spatial;
using Newtonsoft.Json;

namespace MargiesTravel
{
    public partial class TravelIndex
    {
        [System.ComponentModel.DataAnnotations.Key]
        [IsFilterable]
        public string id { get; set; }

        [IsSearchable, IsFilterable, IsSortable]
        public string url { get; set; }

        [IsSearchable]
        [Analyzer(AnalyzerName.AsString.EnLucene)]
        public string file_name { get; set; }

        [IsSearchable, IsFilterable, IsSortable]
        public string content { get; set; }

        [IsSearchable, IsFilterable, IsSortable, IsFacetable]
        public int size { get; set; }

        [IsFilterable, IsSortable, IsFacetable]
        public DateTimeOffset? last_modified { get; set; }
    }
}