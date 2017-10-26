using EPiServer;
using EPiServer.Core;
using EPiServer.Core.Transfer;
using EPiServer.DataAbstraction;
using EPiServer.ServiceLocation;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Http;
using System.Web.Http.Results;
using EPiServer.ServiceApi.Configuration;
using EPiServer.Framework.Blobs;
using System.IO;
using EPiServer.Data.Entity;
using EPiServer.Web.Internal;
using Newtonsoft.Json;
using System.Text;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;

namespace ServiceAPIExtensions.Controllers
{
    [RequireHttps, RoutePrefix("episerverapi/content")]
    public class ContentAPiController : ApiController
    {
        IContentRepository _repo = ServiceLocator.Current.GetInstance<IContentRepository>();
        IContentTypeRepository _typerepo = ServiceLocator.Current.GetInstance<IContentTypeRepository>();
        IRawContentRetriever _rc = ServiceLocator.Current.GetInstance<IRawContentRetriever>();
        IBlobFactory _blobfactory = ServiceLocator.Current.GetInstance<IBlobFactory>();

        readonly static Dictionary<String, ContentReference> constantContentReferenceMap = new Dictionary<string, ContentReference>
        {
            { "", ContentReference.RootPage },
            { "root" , ContentReference.RootPage },
            { "start" , ContentReference.StartPage },
            { "globalblock" , ContentReference.GlobalBlockFolder },
            { "siteblock" , ContentReference.SiteBlockFolder }
        };

        /// <summary>
        /// Finds the content with the given name
        /// </summary>
        /// <param name="reference">The name of the content</param>
        /// <returns>The requested content on success or ContentReference.EmptyReference otherwise</returns>
        ContentReference FindContentReference(string reference)
        {
            if(constantContentReferenceMap.ContainsKey(reference.ToLower()))
            {
                return constantContentReferenceMap[reference.ToLower()];
            }

            if (ContentReference.TryParse(reference, out ContentReference parsedReference))
            {
                return parsedReference;
            }

            if (Guid.TryParse(reference, out Guid parsedGuid))
            {
                return EPiServer.Web.PermanentLinkUtility.FindContentReference(parsedGuid);
            }
            return ContentReference.EmptyReference;
        }

        static Dictionary<string, object> MapContent(IContent content, int recurseContentLevelsRemaining)
        {
            if (content == null)
            {
                return null;
            }
            var result = new Dictionary<string, object>();

            result["Name"] = content.Name;
            result["ParentLink"] = content.ParentLink;
            result["ContentGuid"] = content.ContentGuid;
            result["ContentLink"] = content.ContentLink;
            result["ContentTypeID"] = content.ContentTypeID;
            result["__EpiserverContentType"] = GetContentType(content);

            var binaryContent = content as IBinaryStorable;

            if (binaryContent != null)
            {
                // IF the content has binarydata, get the Hash and size.

                if (binaryContent.BinaryData != null)
                {
                    using (Stream stream = binaryContent.BinaryData.OpenRead())
                    {
                        result.Add("MD5", Hash(stream, MD5.Create()));
                        result.Add("SHA1", Hash(stream, SHA1.Create()));
                        result.Add("SHA256", Hash(stream, SHA256.Create()));
                        result.Add("FileSize", stream.Length);
                    }
                }
                else
                {
                    result.Add("FileSize", 0);
                }

                if (content is MediaData)
                {
                    result.Add("MimeType", (content as MediaData).MimeType);
                }
            }

            foreach(var property in MapProperties(content.Property, recurseContentLevelsRemaining))
            {
                result.Add(property.Key, property.Value);
            }
            
            return result;
        }

        private static Dictionary<string, object> MapProperties(PropertyDataCollection properties, int recurseContentLevelsRemaining)
        {
            var result = new Dictionary<string, object>();
            foreach (var pi in properties.Where(p => p.Value != null))
            {
                if (pi.Type == PropertyDataType.Block)
                {
                    var contentData = pi.Value as IContentData;
                    if (contentData!=null)
                    {
                        result.Add(pi.Name, MapProperties(contentData.Property, recurseContentLevelsRemaining-1));
                    }
                }
                else if (pi is EPiServer.SpecializedProperties.PropertyContentArea)
                {
                    if(recurseContentLevelsRemaining<=0)
                    {
                        continue;
                    }
                    //TODO: Loop through and make array
                    var propertyContentArea = pi as EPiServer.SpecializedProperties.PropertyContentArea;
                    ContentArea contentArea = propertyContentArea.Value as ContentArea;

                    result.Add(pi.Name, contentArea.Items.Select(i => MapContent(i.GetContent(), recurseContentLevelsRemaining-1)).ToList());
                }
                else if (pi.Value is Int32 || pi.Value is Boolean || pi.Value is DateTime || pi.Value is Double || pi.Value is string[] || pi.Value is string)
                {
                    result.Add(pi.Name, pi.Value);
                }
                else
                {
                    //TODO: Handle different return values
                    result.Add(pi.Name, (pi.Value != null) ? pi.ToWebString() : null);
                }
            }

            return result;
        }

        private static string GetContentType(IContent c)
        {
            if(c is MediaData)
            {
                return "File";
            }

            if(c is ContentFolder)
            {
                return "Folder";
            }

            if(c is PageData)
            {
                return "Page";
            }

            if (c is BlockData)
            {
                return "Block";
            }

            return $"Unknown (ContentTypeID={c.ContentTypeID})";
        }

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpPut, Route("entity/{*path}")]
        public virtual IHttpActionResult UpdateContent(string path, [FromBody] Dictionary<string,object> newProperties, EPiServer.DataAccess.SaveAction action = EPiServer.DataAccess.SaveAction.Save)
        {
            path = path ?? "";
            var contentRef = FindContentReference(path);
            if (contentRef == ContentReference.EmptyReference) return NotFound();
            if (contentRef == ContentReference.RootPage) return BadRequest("Cannot update Root entity");

            if(!_repo.TryGet(contentRef, out IContent originalContent))
            {
                return NotFound();
            }

            var content = (originalContent as IReadOnly).CreateWritableClone() as IContent;
            
            EPiServer.DataAccess.SaveAction saveaction = action;
            if (newProperties.ContainsKey("SaveAction") && ((string)newProperties["SaveAction"]) == "Publish")
            {
                saveaction = EPiServer.DataAccess.SaveAction.Publish;
                newProperties.Remove("SaveAction");
            }

            string moveToPath = null;

            if(newProperties.ContainsKey("__EpiserverMoveEntityTo"))
            {
                moveToPath = (string)newProperties["__EpiserverMoveEntityTo"];
                if (!moveToPath.StartsWith("/"))
                {
                    return BadRequest("__EpiserverMoveEntityTo should start with a /");
                }
                newProperties.Remove("__EpiserverMoveEntityTo");
            }

            if(newProperties.ContainsKey("Name"))
            {
                content.Name = newProperties["Name"].ToString();
                newProperties.Remove("Name");
            }
            
            // Store the new information in the object.
            var error = UpdateContentProperties(newProperties, content);
            if (!string.IsNullOrEmpty(error)) return BadRequest($"Invalid property '{error}'");

            if (moveToPath != null)
            {
                try
                {
                    var moveTo = FindContentReference(moveToPath.Substring(1));
                    _repo.Move(contentRef, moveTo);
                }
                catch (ContentNotFoundException)
                {
                    return BadRequest("target page not found");
                }
            }
            // Save the reference and publish if requested.
            try
            {
                var updatedReference = _repo.Save(content, saveaction);
                return Ok(new { reference = updatedReference.ToString() });
            }
            catch(ValidationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpPost, Route("entity/{*path}")]
        public virtual IHttpActionResult CreateContent(string path, [FromBody] Dictionary<string,object> contentProperties, EPiServer.DataAccess.SaveAction action = EPiServer.DataAccess.SaveAction.Save)
        {
            path = path ?? "";
            var parentContentRef = FindContentReference(path);
            if (parentContentRef == ContentReference.EmptyReference) return NotFound();
            if(!ReferenceExists(parentContentRef))
            {
                return NotFound();
            }

            // Instantiate content of named type.
            if (contentProperties == null)
            {
                return BadRequest("No properties specified");
            }

            if (!contentProperties.TryGetValue("ContentType", out object contentTypeString) || !(contentTypeString is string))
            {
                return BadRequest("'ContentType' is a required field.");
            }

            // Check ContentType.
            ContentType contentType = FindEpiserverContentType(contentTypeString);
            if (contentType == null)
            {
                return BadRequest($"'{contentTypeString}' is an invalid ContentType");
            }
            contentProperties.Remove("ContentType");

            if (!contentProperties.TryGetValue("Name", out object nameValue) || !(nameValue is string))
            {
                return BadRequest("Name is a required field");
            }
            contentProperties.Remove("Name");
            
            EPiServer.DataAccess.SaveAction saveaction = action;
            if (contentProperties.ContainsKey("SaveAction") && (string)contentProperties["SaveAction"] == "Publish")
            {
                saveaction = EPiServer.DataAccess.SaveAction.Publish;
                contentProperties.Remove("SaveAction");
            }
            
            // Create content.
            IContent content = _repo.GetDefault<IContent>(parentContentRef, contentType.ID);

            content.Name = (string)nameValue;

            // Set all the other values.
            var error = UpdateContentProperties(contentProperties, content);
            if (!string.IsNullOrEmpty(error)) return BadRequest($"Invalid property '{error}'");

            // Save the reference with the requested save action.
            try
            {
                var createdReference = _repo.Save(content, saveaction);
                return Created(path, new { reference = createdReference.ID });
            }
            catch (ValidationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        private bool ReferenceExists(ContentReference contentRef)
        {
            return _repo.TryGet(contentRef, out IContent cont);
        }

        private ContentType FindEpiserverContentType(object contentTypeString)
        {
            var contentType = _typerepo.Load((string)contentTypeString);

            if(contentType!=null)
            {
                return contentType;
            }

            if(int.TryParse((string)contentTypeString, out int contentTypeId)) {
                return _typerepo.Load(contentTypeId);
            }
            
            return null;
        }

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpDelete, Route("entity/{*path}")]
        public virtual IHttpActionResult DeleteContent(string path)
        {
            path = path ?? "";
            var contentReference = FindContentReference(path);
            if (contentReference == ContentReference.EmptyReference) return NotFound();
            if (contentReference == ContentReference.RootPage && string.IsNullOrEmpty(path)) return BadRequest("'root' can only be deleted by specifying its name in the path!");

            try
            {
                _repo.MoveToWastebasket(contentReference);
            }
            catch(ContentNotFoundException e)
            {
                return NotFound();
            }
            return Ok();
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("binary/{*path}")]
        public virtual IHttpActionResult GetBinaryContent(string path)
        {
            path = path ?? "";
            var contentRef = FindContentReference(path);
            if (contentRef == ContentReference.EmptyReference) return NotFound();
                
            if(!_repo.TryGet(contentRef, out IContent content)) {
                return NotFound();
            }

            if (content is IBinaryStorable)
            {
                var binary = content as IBinaryStorable;
                if (binary.BinaryData == null) return NotFound();

                // Return the binary contents as a stream.
                using (var br = new BinaryReader(binary.BinaryData.OpenRead()))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Content = new ByteArrayContent(br.ReadBytes((int)br.BaseStream.Length));
                    if (content as IContentMedia != null)
                    {
                        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue((content as IContentMedia).MimeType);
                    }
                    else
                    {
                        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    }
                    return ResponseMessage(response);
                }
            }

            if (content is PageData)
            {
                var page = content as PageData;

                string url = string.Format("{0}://{1}:{2}{3}",
                        HttpContext.Current.Request.Url.Scheme,
                        HttpContext.Current.Request.Url.Host,
                        HttpContext.Current.Request.Url.Port,
                        page.Property["PageLinkUrl"].ToString()
                        );

                return Redirect(url);
            }

            return StatusCode(HttpStatusCode.NoContent);
        }

        const int GetChildrenRecurseContentLevel = 1;

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("children/{*path}")]
        public virtual IHttpActionResult GetChildren(string path)
        {
            path = path ?? "";
            var contentReference = FindContentReference(path);
            if (contentReference == ContentReference.EmptyReference) return NotFound();

            if (!_repo.TryGet(contentReference, out IContent parentContent)) {
                return NotFound();
            }

            var children = new List<Dictionary<string, object>>();

            // Collect sub pages
            children.AddRange(_repo.GetChildren<IContent>(contentReference).Select(x => MapContent(x, recurseContentLevelsRemaining: GetChildrenRecurseContentLevel)));

            if (parentContent is PageData)
            {
                children.AddRange(
                    parentContent.Property
                    .Where(p => p.Value != null && p.Value is ContentArea)
                    .Select(p=>p.Value as ContentArea)
                    .SelectMany(ca => ca.Items.Select(item=> MapContent(_repo.Get<IContent>(item.ContentLink), recurseContentLevelsRemaining: GetChildrenRecurseContentLevel))));
            }

            return Ok(children.ToArray());
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("entity/{*path}")]
        public virtual IHttpActionResult GetEntity(string path)
        {
            path = path ?? "";
            var contentReference = FindContentReference(path);
            if (contentReference == ContentReference.EmptyReference) return NotFound();

            try
            {
                var content = _repo.Get<IContent>(contentReference);
                if (content.IsDeleted) return NotFound();
                return Ok(MapContent(content,recurseContentLevelsRemaining:1));
            }
            catch(ContentNotFoundException)
            {
                return NotFound();
            }
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("type-by-entity/{pageId}")]
        public virtual IHttpActionResult GetContentTypeByPageId(string pageId)
        {
            pageId = pageId ?? "";

            var contentRef = FindContentReference(pageId);

            if (contentRef.Equals(ContentReference.EmptyReference))
            {
                return NotFound();
            }

            if (!_repo.TryGet(contentRef, out IContent content))
            {
                return NotFound();
            }

            return EpiserverContentTypeResult(content);
        }


        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("type/{Type}")]
        public virtual IHttpActionResult GetContentType(string Type)
        {
            var episerverType = _typerepo.Load(Type);

            if(episerverType==null)
            {
                return NotFound();
            }

            var page = _repo.GetDefault<IContent>(ContentReference.RootPage, episerverType.ID);

            return EpiserverContentTypeResult(page);
        }

        private IHttpActionResult EpiserverContentTypeResult(IContent content)
        {
            return new JsonResult<object>(
                            new
                            {
                                TypeName = content.GetOriginalType().Name,
                                Properties = content.Property.Select(p => new { Name = p.Name, Type = p.Type.ToString() })
                            },
                            new JsonSerializerSettings(),
                            Encoding.UTF8,
                            this);
        }

        private void WriteBlobToStorage(string name, byte[] data, MediaData md)
        {
            var extension = Path.GetExtension(name);

            if(string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            var blob = _blobfactory.CreateBlob(md.BinaryDataContainer, extension);
            using (var writer = new BinaryWriter(blob.OpenWrite()))
            {
                writer.Write(data);
                writer.Flush(); // this is probably not necessary because of the dispose
            }
            md.BinaryData = blob;
        }

        private string UpdateContentProperties(IDictionary<string, object> newProperties, IContent content)
        {
            foreach (var propertyName in newProperties.Keys)
            {
                var errorMessage = UpdateFieldOnContent(content, content.Name ?? (string)newProperties["Name"],  propertyName, newProperties[propertyName]);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return errorMessage;
                }
            }
            return null;
        }

        private string UpdateFieldOnContent(IContent con, string contentName, string propertyName, object value)
        {
            //Problem: con might only contain very few properties (not inherited)
            if (con.Property.Contains(propertyName))
            {

                if (con.Property[propertyName] is EPiServer.SpecializedProperties.PropertyContentArea)
                {
                    //Handle if property is Content Area.
                    if (con.Property[propertyName].Value == null) con.Property[propertyName].Value = new ContentArea();
                    ContentArea ca = con.Property[propertyName].Value as ContentArea;
                    var lst = value as List<object>;
                    if (lst != null)
                    {
                        foreach (var s in (lst as List<object>))
                        {
                            var itmref = FindContentReference(s.ToString());
                            ca.Items.Add(new ContentAreaItem() { ContentLink = itmref });
                        }
                    }
                }
                else if (value is string[])
                {
                    con.Property[propertyName].Value = value as string[];
                }
                else if (con.Property[propertyName].GetType() == typeof(EPiServer.Core.PropertyDate))
                {
                    if (value is DateTime)
                    {
                        con.Property[propertyName].Value = value;
                    }
                    else
                    {
                        con.Property[propertyName].ParseToSelf((string)value);
                    }
                }
                else
                {
                    con.Property[propertyName].Value = value;
                }
                return null;
            }

            if (propertyName.ToLower() == "binarydata" && con is MediaData)
            {
                byte[] bytes = Convert.FromBase64String((string)value);
                WriteBlobToStorage(contentName, bytes, con as MediaData);
                return null;
            }

            return propertyName;
        }
                
        private static string Hash(Stream stream, HashAlgorithm hashing)
        {
            StringBuilder sBuilder = new StringBuilder();
            stream.Position = 0;

            byte[] hash = hashing.ComputeHash(stream);
            for (int i = 0; i < hash.Length; i++)
            {
                sBuilder.Append(hash[i].ToString("x2"));
            }

            return sBuilder.ToString();
        }
    }
}