using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;

/// <summary>
/// A simple C# wrapper for Vimeo video uploading.
/// </summary>
public class Vimeo {
	#region Constructor and Local Properties

	/// <summary>
	/// Access Token for authorized user.
	/// </summary>
	public string AccessToken { get; private set; }

	/// <summary>
	/// Init a new instance of the Vimeo wrapper.
	/// </summary>
	/// <param name="accessToken">Access Token for authorized user.</param>
	public Vimeo(string accessToken) {
		this.AccessToken = accessToken;
	}

	#endregion
	#region Helper Functions

	/// <summary>
	/// Do a API request.
	/// </summary>
	/// <param name="uri">Uri to access.</param>
	/// <param name="method">HTTP method to use.</param>
	/// <param name="payload">JSON object to send.</param>
	/// <param name="bytes">Bytes to transfer.</param>
	/// <param name="additionalHeaders">Additional headers to use.</param>
	/// <returns>Response object.</returns>
	public VimeoResponse Request(string uri, string method = "POST", object payload = null, byte[] bytes = null, NameValueCollection additionalHeaders = null) {
		const string apiBaseUri = "https://api.vimeo.com";

		if (!uri.StartsWith("https")) {
			uri = apiBaseUri + uri;
		}

		var req = WebRequest.Create(uri) as HttpWebRequest;

		if (req == null) {
			throw new Exception("Coult not establish a WebRequest to " + uri);
		}

		string json;

		req.Accept = "application/vnd.vimeo.*+json; version=3.2";
		req.Method = method;
		req.UserAgent = ".NET Vimeo API Client";

		if (uri.StartsWith(apiBaseUri)) {
			req.Headers.Add("Authorization", "Bearer " + this.AccessToken);
		}

		if (additionalHeaders != null) {
			foreach (var header in additionalHeaders) {
				req.Headers.Add(header.ToString(), additionalHeaders[header.ToString()]);
			}
		}

		if (payload != null) {
			if (payload is string) {
				json = (string) payload;
			}
			else {
				json = JsonConvert.SerializeObject(
					payload,
					Formatting.None,
					new JsonSerializerSettings {
						NullValueHandling = NullValueHandling.Ignore
					});
			}

			var buffer = Encoding.UTF8.GetBytes(json);

			req.ContentType = "application/json";
			req.ContentLength = buffer.Length;

			var stream = req.GetRequestStream();

			stream.Write(buffer, 0, buffer.Length);
			stream.Close();
		}
		else if (bytes != null) {
			req.ContentLength = bytes.Length;

			var stream = req.GetRequestStream();

			stream.Write(bytes, 0, bytes.Length);
			stream.Close();
		}
		else {
			req.ContentLength = 0;
		}

		try {
			var res = req.GetResponse() as HttpWebResponse;

			if (res == null) {
				throw new Exception("Could not get response from request.");
			}

			var stream = res.GetResponseStream();

			if (stream == null) {
				throw new Exception("Coult not get stream from response.");
			}

			var reader = new StreamReader(stream);

			json = reader.ReadToEnd();
			reader.Close();

			return new VimeoResponse {
				Code = (int)res.StatusCode,
				Headers = res.Headers,
				JSON = json
			};
		}
		catch (WebException ex) {
			var res = ex.Response as HttpWebResponse;

			if (res == null) {
				throw new Exception("Could not get response from request.");
			}

			var stream = res.GetResponseStream();

			if (stream == null) {
				throw new Exception("Coult not get stream from response.");
			}

			var reader = new StreamReader(stream);

			json = reader.ReadToEnd();
			reader.Close();

			return new VimeoResponse {
				Code = (int)res.StatusCode,
				Headers = res.Headers,
				Exception = ex,
				JSON = json
			};
		}
	}

	#endregion
	#region Class Functions

	/// <summary>
	/// Upload a local file to Vimeo.
	/// </summary>
	/// <param name="filePath">Path to the file.</param>
	/// <param name="properties">Properties to patch the file with after upload.</param>
	/// <returns>Video info.</returns>
	public VimeoUploadResponse UploadFile(string filePath, VimeoVideoProperties properties = null) {
		var ur = new VimeoUploadResponse {
			Exceptions = new List<Exception>()
		};

        // Generate a ticket.
        var res = this.Request("/me/videos", "POST", new { type = "streaming" });

        if (res.Code != 201) {
	        ur.Exceptions.Add(res.Exception);
	        return ur;
        }

        var ticket = JsonConvert.DeserializeObject<VimeoUploadTicket>(res.JSON);

        if (ticket == null) {
            ur.Exceptions.Add(new Exception("Unable to deserialize ticket JSON response from Vimeo."));
	        return ur;
        }

		ur.Ticket = ticket;

        // Cycle and upload chunks.
        var fileInfo = new FileInfo(filePath);
        var currentPos = 0L;
        var totalLength = fileInfo.Length;

        var bytes = File.ReadAllBytes(filePath);

        while(currentPos<totalLength) {
            // Upload chunks.
            var buffer = new List<byte>();

	        for (var i = 0; i < bytes.Length; i++) {
		        if (i < currentPos) {
			        continue;
		        }

		        buffer.Add(bytes[i]);
	        }

	        res = this.Request(
		        ticket.upload_link_secure,
		        "PUT",
		        null,
		        buffer.ToArray(),
		        new WebHeaderCollection {
			        {"Content-Range", string.Format("bytes {0}-{1}/{1}", currentPos, totalLength)}
		        });

	        if (res.Exception != null) {
		        ur.Exceptions.Add(res.Exception);
	        }

            // Check progress.
            res = Request(
                ticket.upload_link_secure,
                "PUT",
                null,
                null,
                new WebHeaderCollection {
                    { "Content-Range", "bytes */*" }
                });

	        if (res.Headers == null) {
		        ur.Exceptions.Add(new Exception("API returned invalid response for PUT (check progress)."));
				return ur;
	        }

			if (res.Exception != null) {
				ur.Exceptions.Add(res.Exception);
			}

	        var range = res.Headers["Range"];
	        var rangeSepIndex = range.IndexOf("-", StringComparison.InvariantCultureIgnoreCase);

	        if (rangeSepIndex <= -1)
		        continue;

	        long toBytes;

	        if (!long.TryParse(range.Substring(rangeSepIndex + 1), out toBytes))
		        continue;

	        if (toBytes == totalLength) {
		        break;
	        }

	        currentPos = toBytes;
        }

        // Mark upload as completed.
        res = this.Request(
            ticket.complete_uri,
            "DELETE");

		if (res.Exception != null) {
			ur.Exceptions.Add(res.Exception);
		}

		var videoID = res.Headers["Location"].Substring(res.Headers["Location"].LastIndexOf("/", StringComparison.InvariantCultureIgnoreCase) + 1);

		if (properties != null) {
			res = this.Request(
				res.Headers["Location"],
				"PATCH",
				properties);

			if (res.Exception != null) {
				ur.Exceptions.Add(res.Exception);
			}
		}

		res = this.Request("/me/videos/" + videoID, "GET");

		if (res.Exception != null) {
			ur.Exceptions.Add(res.Exception);
		}

		ur.Video = JsonConvert.DeserializeObject<VimeoVideo>(res.JSON);

		return ur;
	}

	/// <summary>
	/// Generate a Vimeo upload ticket.
	/// </summary>
	/// <returns>Upload ticket.</returns>
	public VimeoUploadTicket GetUploadTicket() {
		var res = this.Request("/me/videos", "POST", new { type = "streaming" });
		var ticket = JsonConvert.DeserializeObject<VimeoUploadTicket>(res.JSON);

		return ticket;
	}

	/// <summary>
	/// Get metadata for a single video.
	/// </summary>
	/// <param name="videoID">The Vimeo ID of the video.</param>
	/// <returns>Video metadata.</returns>
	public VimeoVideo GetVideo(string videoID) {
		var res = this.Request("/me/videos/" + videoID, "GET");
		var video = JsonConvert.DeserializeObject<VimeoVideo>(res.JSON);

		return video;
	}

	/// <summary>
	/// Get a list of all videos' metadata.
	/// </summary>
	/// <param name="query">String to filter videos by.</param>
	/// <returns>List of videos' metadata.</returns>
	public List<VimeoVideo> GetVideos(string query = null) {
		var res = this.Request("/me/videos?direction=desc&per_page=100&sort=date" + (query != null ? "&query=" + query : null), "GET");
		var page = JsonConvert.DeserializeObject<VimeoPage>(res.JSON);
		var list = page.data;

		while (true) {
			if (page.paging.next == null) {
				break;
			}

			res = this.Request(page.paging.next, "GET");
			page = JsonConvert.DeserializeObject<VimeoPage>(res.JSON);

			list.AddRange(page.data);
		}

		return list;
	} 

	#endregion
	#region Enums

	public enum VimeoVideoEmbedTitleName {
		[JsonProperty(PropertyName = "user")]
		User,

		[JsonProperty(PropertyName = "show")]
		Show,

		[JsonProperty(PropertyName = "hide")]
		Hide
	}

	public enum VimeoVideoEmbedTitleOwner {
		[JsonProperty(PropertyName = "user")]
		User,

		[JsonProperty(PropertyName = "show")]
		Show,

		[JsonProperty(PropertyName = "hide")]
		Hide
	}

	public enum VimeoVideoEmbedTitlePortrait {
		[JsonProperty(PropertyName = "user")]
		User,

		[JsonProperty(PropertyName = "show")]
		Show,

		[JsonProperty(PropertyName = "hide")]
		Hide
	}

	public enum VimeoVideoLicense {
		[JsonProperty(PropertyName = "by")]
		By,

		[JsonProperty(PropertyName = "by-sa")]
		BySa,

		[JsonProperty(PropertyName = "by-nd")]
		ByNd,

		[JsonProperty(PropertyName = "by-nc")]
		ByNc,

		[JsonProperty(PropertyName = "by-nc-sa")]
		ByNcSa,

		[JsonProperty(PropertyName = "by-nc-nd")]
		ByNcNd,

		[JsonProperty(PropertyName = "cc0")]
		Cc0
	}

	public enum VimeoVideoPrivacyComments {
		[JsonProperty(PropertyName = "anybody")]
		Anybody,

		[JsonProperty(PropertyName = "nobody")]
		Nobody,

		[JsonProperty(PropertyName = "contacts")]
		Contacts
	}

	public enum VimeoVideoPrivacyEmbed {
		[JsonProperty(PropertyName = "public")]
		Public,

		[JsonProperty(PropertyName = "private")]
		Private,

		[JsonProperty(PropertyName = "whitelist")]
		Whitelist
	}

	public enum VimeoVideoPrivacyView {
		[JsonProperty(PropertyName = "anybody")]
		Anybody,

		[JsonProperty(PropertyName = "nobody")]
		Nobody,

		[JsonProperty(PropertyName = "contacts")]
		Contacts,

		[JsonProperty(PropertyName = "password")]
		Password,

		[JsonProperty(PropertyName = "users")]
		Users,

		[JsonProperty(PropertyName = "unlisted")]
		Unlisted,

		[JsonProperty(PropertyName = "disable")]
		Disable
	}

	public enum VimeoVideoRatingsMPAARating {
		[JsonProperty(PropertyName = "g")]
		G,

		[JsonProperty(PropertyName = "pg")]
		PG,

		[JsonProperty(PropertyName = "pg13")]
		PG13,

		[JsonProperty(PropertyName = "r")]
		R,

		[JsonProperty(PropertyName = "nc17")]
		NC17,

		[JsonProperty(PropertyName = "x")]
		X
	}

	public enum VimeoVideoRatingsMPAAReason {
		[JsonProperty(PropertyName = "at")]
		at,

		[JsonProperty(PropertyName = "n")]
		n,

		[JsonProperty(PropertyName = "bn")]
		bn,

		[JsonProperty(PropertyName = "ss")]
		ss,

		[JsonProperty(PropertyName = "sl")]
		sl,

		[JsonProperty(PropertyName = "v")]
		v
	}

	public enum VimeoVideoRatingsTVRating {
		[JsonProperty(PropertyName = "tv-y")]
		y,

		[JsonProperty(PropertyName = "tv-y7")]
		y7,

		[JsonProperty(PropertyName = "tv-y7-fv")]
		y7fv,

		[JsonProperty(PropertyName = "tv-g")]
		g,

		[JsonProperty(PropertyName = "tv-pg")]
		pg,

		[JsonProperty(PropertyName = "tv-14")]
		t14,

		[JsonProperty(PropertyName = "tv-ma")]
		ma
	}

	public enum VimeoVideoRatingsTVReason {
		[JsonProperty(PropertyName = "d")]
		d,

		[JsonProperty(PropertyName = "fv")]
		fv,

		[JsonProperty(PropertyName = "l")]
		l,

		[JsonProperty(PropertyName = "ss")]
		ss,

		[JsonProperty(PropertyName = "v")]
		v
	}

	public enum VimeoVideoSpatialProjection {
		[JsonProperty(PropertyName = "dome")]
		Dome,

		[JsonProperty(PropertyName = "cubical")]
		Cubical,

		[JsonProperty(PropertyName = "cylindrical")]
		Cylindrical,

		[JsonProperty(PropertyName = "equirectangular")]
		Equirectangular,

		[JsonProperty(PropertyName = "pyramid")]
		Pyramid
	}

	public enum VimeoVideoSpatialStereoFormat {
		[JsonProperty(PropertyName = "left-right")]
		LeftRight,

		[JsonProperty(PropertyName = "mono")]
		Mono,

		[JsonProperty(PropertyName = "top-bottom")]
		TopBottom
	}

	#endregion
	#region Helper Classes

	public class VimeoPage {
		public int total { get; set; }
		public int page { get; set; }
		public int per_page { get; set; }
		public VimeoPaging paging { get; set; }
		public List<VimeoVideo> data { get; set; }
	}

	public class VimeoPaging {
		public string next { get; set; }
		public string previous { get; set; }
		public string first { get; set; }
		public string last { get; set; }
	}

	public class VimeoResponse {
		public int Code { get; set; }
		public Exception Exception { get; set; }
		public string JSON { get; set; }
		public WebHeaderCollection Headers { get; set; }
	}

	public class VimeoUploadResponse {
		public List<Exception> Exceptions { get; set; }
		public VimeoUploadTicket Ticket { get; set; }
		public VimeoVideo Video { get; set; }
	}

	public class VimeoUploadTicket {
		public string uri { get; set; }
		public string ticket_id { get; set; }
		public string complete_uri { get; set; }
		public string upload_link_secure { get; set; }
		public object user { get; set; }
	}

	public class VimeoVideo {
		public string uri { get; set; }
		public string name { get; set; }
		public string description { get; set; }
		public string link { get; set; }
		public int duration { get; set; }
		public int width { get; set; }
		public string language { get; set; }
		public int height { get; set; }
		public VimeoVideoEmbed embed { get; set; }
		public DateTime created_time { get; set; }
		public DateTime modified_time { get; set; }
		public DateTime release_time { get; set; }
		public List<string> content_rating { get; set; }
		public string license { get; set; }
		public VimeoVideoPrivacy privacy { get; set; }
		public VimeoVideoPictures pictures { get; set; }
		public List<string> tags { get; set; }
		public VimeoVideoStats stats { get; set; }
		public VimeoVideoMetadata metadata { get; set; }
		public object user { get; set; }
		public string status { get; set; }
		public string resource_key { get; set; }
		public object embed_presets { get; set; }
	}

	public class VimeoVideoEmbed {
		public string uri { get; set; }
		public string html { get; set; }
		public VimeoVideoEmbedButtons buttons { get; set; }
		public VimeoVideoEmbedLogos logos { get; set; }
		public VimeoVideoEmbedTitle title { get; set; }
		public bool playbar { get; set; }
		public bool volume { get; set; }
		public string color { get; set; }
	}

	public class VimeoVideoEmbedButtons {
		public bool like { get; set; }
		public bool watchlater { get; set; }
		public bool share { get; set; }
		public bool embed { get; set; }
		public bool hd { get; set; }
		public bool fullscreen { get; set; }
		public bool scaling { get; set; }
	}

	public class VimeoVideoEmbedLogos {
		public bool vimeo { get; set; }
		public VimeoVideoEmbedLogosCustom custom { get; set; }
	}

	public class VimeoVideoEmbedLogosCustom {
		public bool active { get; set; }
		public string link { get; set; }
		public bool sticky { get; set; }
	}

	public class VimeoVideoEmbedTitle {
		public string name { get; set; }
		public string owner { get; set; }
		public string portrait { get; set; }
	}

	public class VimeoVideoPrivacy {
		public string view { get; set; }
		public string embed { get; set; }
		public bool download { get; set; }
		public bool add { get; set; }
		public string comments { get; set; }
	}

	public class VimeoVideoPictures {
		public string uri { get; set; }
		public bool active { get; set; }
		public string type { get; set; }
		public List<VimeoVideoPicturesSizes> sizes { get; set; }
		public string resource_key { get; set; }
	}

	public class VimeoVideoPicturesSizes {
		public int width { get; set; }
		public int height { get; set; }
		public string link { get; set; }
		public string link_with_play_button { get; set; }
	}

	public class VimeoVideoStats {
		public int plays { get; set; }
	}

	public class VimeoVideoMetadata {
		public VimeoVideoMetadataConnections connections { get; set; }
		public VimeoVideoMetadataInteractions interactions { get; set; }
	}

	public class VimeoVideoMetadataConnections {
		public VimeoVideoMetadataConnectionsSub comments { get; set; }
		public VimeoVideoMetadataConnectionsSub credits { get; set; }
		public VimeoVideoMetadataConnectionsSub likes { get; set; }
		public VimeoVideoMetadataConnectionsSub pictures { get; set; }
		public VimeoVideoMetadataConnectionsSub texttracks { get; set; }
		public VimeoVideoMetadataConnectionsSub related { get; set; }
	}

	public class VimeoVideoMetadataConnectionsSub {
		public string uri { get; set; }
		public List<string> options { get; set; }
		public int total { get; set; }
	}

	public class VimeoVideoMetadataInteractions {
		public VimeoVideoMetadataInteractionsWatchLater watchlater { get; set; }
	}

	public class VimeoVideoMetadataInteractionsWatchLater {
		public bool added { get; set; }
		public DateTime? added_time { get; set; }
		public string uri { get; set; }
	}

	/// <summary>
	/// Properties to set on a video after it's uploaded to Vimeo.
	/// </summary>
	public class VimeoVideoProperties {
		/// <summary>
		/// A list of values describing the content in this video.
		/// </summary>
		[JsonProperty(PropertyName = "content_rating")]
		public string ContentRating { get; set; }

		/// <summary>
		/// The new description for the video.
		/// </summary>
		[JsonProperty(PropertyName = "description")]
		public string Description { get; set; }

		/// <summary>
		/// Show or hide the embed button.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.embed")]
		public bool? EmbedButtonsEmbed { get; set; }

		/// <summary>
		/// Show or hide the fullscreen button.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.fullscreen")]
		public bool? EmbedButtonsFullscreen { get; set; }

		/// <summary>
		/// Show or hide the HD button.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.hd")]
		public bool? EmbedButtonsHD { get; set; }

		/// <summary>
		/// Show or hide the like button.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.like")]
		public bool? EmbedButtonsLike { get; set; }

		/// <summary>
		/// Show or hide the scaling button. Shown only in fullscreen mode.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.scaling")]
		public bool? EmbedButtonsScaling { get; set; }

		/// <summary>
		/// Show or hide the share button.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.share")]
		public bool? EmbedButtonsShare { get; set; }

		/// <summary>
		/// Show or hide the watch later button.
		/// </summary>
		[JsonProperty(PropertyName = "embed.buttons.watchlater")]
		public bool? EmbedButtonsWatchLater { get; set; }

		/// <summary>
		/// A primary color used by the embed player.
		/// </summary>
		[JsonProperty(PropertyName = "embed.color")]
		public string EmbedColor { get; set; }

		/// <summary>
		/// Show or hide your custom logo.
		/// </summary>
		[JsonProperty(PropertyName = "embed.logos.custom.active")]
		public bool? EmbedLogosCustomActive { get; set; }

		/// <summary>
		/// A URL that your user will navigate to if they click your custom logo.
		/// </summary>
		[JsonProperty(PropertyName = "embed.logos.custom.link")]
		public string EmbedLogosCustomLink { get; set; }

		/// <summary>
		/// Always show the custom logo, or hide it after time with the rest of the UI.
		/// </summary>
		[JsonProperty(PropertyName = "embed.logos.custom.sticky")]
		public bool? EmbedLogosCustomSticky { get; set; }

		/// <summary>
		/// Show or hide the vimeo logo.
		/// </summary>
		[JsonProperty(PropertyName = "embed.logos.vimeo")]
		public bool? EmbedLogosVimeo { get; set; }

		/// <summary>
		/// Show or hide the playbar.
		/// </summary>
		[JsonProperty(PropertyName = "embed.playbar")]
		public bool? EmbedPlaybar { get; set; }

		/// <summary>
		/// Show, hide, or let the user decide if the video title shows on the video.
		/// </summary>
		[JsonProperty(PropertyName = "embed.title.name")]
		public VimeoVideoEmbedTitleName? EmbedTitleName { get; set; }

		/// <summary>
		/// Show, hide, or let the user decide if the owners information shows on the video.
		/// </summary>
		[JsonProperty(PropertyName = "embed.title.owner")]
		public VimeoVideoEmbedTitleOwner? EmbedTitleOwner { get; set; }

		/// <summary>
		/// Show, hide, or let the user decide if the owners portrait shows on the video.
		/// </summary>
		[JsonProperty(PropertyName = "embed.title.portrait")]
		public VimeoVideoEmbedTitlePortrait? EmbedTitlePortrait { get; set; }

		/// <summary>
		/// Show or hide the volume selector.
		/// </summary>
		[JsonProperty(PropertyName = "embed.volume")]
		public bool? EmbedVolume { get; set; }

		/// <summary>
		/// External data from IMDb.
		/// </summary>
		[JsonProperty(PropertyName = "external_links.imdb")]
		public string ExternalLinksIMDb { get; set; }

		/// <summary>
		/// External data from Rotten Tomatoes.
		/// </summary>
		[JsonProperty(PropertyName = "external_links.rotten_tomatoes")]
		public string ExternalLinksRottenTomatoes { get; set; }

		/// <summary>
		/// Set the Creative Commons license.
		/// </summary>
		[JsonProperty(PropertyName = "license")]
		public VimeoVideoLicense? License { get; set; }

		/// <summary>
		/// Set the default language for this video.
		/// </summary>
		[JsonProperty(PropertyName = "locale")]
		public string Locale { get; set; }

		/// <summary>
		/// The new title for the video.
		/// </summary>
		[JsonProperty(PropertyName = "name")]
		public string Name { get; set; }

		/// <summary>
		/// When you set privacy.view to password, you must provide the password as an additional parameter.
		/// </summary>
		[JsonProperty(PropertyName = "password")]
		public string Password { get; set; }

		/// <summary>
		/// Enable or disable the ability for anyone to add the video to an album, channel, or group.
		/// </summary>
		[JsonProperty(PropertyName = "privacy.add")]
		public bool? PrivacyAdd { get; set; }

		/// <summary>
		/// The privacy for who can comment on the video.
		/// </summary>
		[JsonProperty(PropertyName = "privacy.comments")]
		public VimeoVideoPrivacyComments? PrivacyComments { get; set; }

		/// <summary>
		/// Enable or disable the ability for anyone to download video.
		/// </summary>
		[JsonProperty(PropertyName = "privacy.download")]
		public bool? PrivacyDownload { get; set; }

		/// <summary>
		/// The videos new embed settings. Whitelist allows you to define all valid embed domains.
		/// </summary>
		[JsonProperty(PropertyName = "privacy.embed")]
		public VimeoVideoPrivacyEmbed? PrivacyEmbed { get; set; }

		/// <summary>
		/// The new privacy setting for the video.
		/// </summary>
		[JsonProperty(PropertyName = "privacy.view")]
		public VimeoVideoPrivacyView? PrivacyView { get; set; }

		/// <summary>
		/// Set MPAA rating for a video.
		/// </summary>
		[JsonProperty(PropertyName = "ratings.mpaa.rating")]
		public VimeoVideoRatingsMPAARating? RatingsMPAARating { get; set; }

		/// <summary>
		/// Set MPAA rating reason for a video.
		/// </summary>
		[JsonProperty(PropertyName = "ratings.mpaa.reason")]
		public VimeoVideoRatingsMPAAReason? RatingsMPAAReason { get; set; }

		/// <summary>
		/// Set TV rating for a video.
		/// </summary>
		[JsonProperty(PropertyName = "ratings.tv.rating")]
		public VimeoVideoRatingsTVRating? RatingsTVRating { get; set; }

		/// <summary>
		/// Set TV rating reason for a video
		/// </summary>
		[JsonProperty(PropertyName = "ratings.tv.reason")]
		public VimeoVideoRatingsTVReason? RatingsTVReason { get; set; }

		/// <summary>
		/// Enable or disable the review page.
		/// </summary>
		[JsonProperty(PropertyName = "review_link")]
		public bool? ReviewLink { get; set; }

		/// <summary>
		/// 360 director timeline. The arrays in this should include a "time_code", "pitch", "yaw", and optionally "roll". For pitch, the minimum allowed is -90, and the max of 90. For yaw, the minimum is 0, and a maximum of 360.
		/// </summary>
		[JsonProperty(PropertyName = "spatial.director_timeline")]
		public string SpatialDirectorTimeline { get; set; }

		/// <summary>
		/// 360 field of view. Default 50, min 30, max 90.
		/// </summary>
		[JsonProperty(PropertyName = "spatial.field_of_view")]
		public string SpatialFieldOfView { get; set; }

		/// <summary>
		/// 360 spatial projection.
		/// </summary>
		[JsonProperty(PropertyName = "spatial.projection")]
		public VimeoVideoSpatialProjection? SpatialProjection { get; set; }

		/// <summary>
		/// 360 spatial stereo format.
		/// </summary>
		[JsonProperty(PropertyName = "spatial.stereo_format")]
		public VimeoVideoSpatialStereoFormat? SpatialStereoFormat { get; set; }
	}

	#endregion
}