using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Represents NFT-related information for a recording.
    /// </summary>
    public class EncoraNft
    {
        /// <summary>
        /// Gets or sets the NFT date, if applicable.
        /// </summary>
        [JsonPropertyName("nft_date")]
        public string? NftDate { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the NFT is forever.
        /// </summary>
        [JsonPropertyName("nft_forever")]
        public bool NftForever { get; set; }
    }
}
