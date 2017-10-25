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
        ContentReference FindContentReferenceByString(string reference)
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

        /// <summary>
        /// Finds the content with a given name of its parent. Favours URLEncoded name over actual name.
        /// </summary>
        /// <param name="Parent">The reference to the parent</param>
        /// <param name="Name">The name of the content</param>
        /// <returns>The requested content on success or ContentReference.EmptyReference otherwise</returns>
        protected ContentReference LookupRef(ContentReference Parent, string Name)
        {
            if (Parent.Equals(ContentReference.EmptyReference))
            {
                return ContentReference.EmptyReference;
            }

            var content = (new UrlSegment(_repo)).GetContentBySegment(Parent, Name);
            if (content != null && !content.Equals(ContentReference.EmptyReference))
            {
                return content;
            }
            
            var temp = _repo.GetChildren<IContent>(Parent).Where(ch => SegmentedName(ch.Name) == Name).FirstOrDefault();
            if (temp != null)
            {
                return temp.ContentLink;
            }

            return ContentReference.EmptyReference;
        }
        
        public static Dictionary<string, object> MapContent(IContent content)
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

            if (binaryContent!=null)
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

            foreach (var pi in content.Property.Where(p => p.Value != null))
            {
                if (pi.Type == PropertyDataType.Block && pi.Value is IContent)
                {
                    //TODO: Doesn't work. Check SiteLogoType on start page
                    result.Add(pi.Name, MapContent((IContent)pi.Value));
                }
                else if (pi is EPiServer.SpecializedProperties.PropertyContentArea)
                {
                    //TODO: Loop through and make array
                    var propertyContentArea = pi as EPiServer.SpecializedProperties.PropertyContentArea;
                    ContentArea contentArea = propertyContentArea.Value as ContentArea;
                    
                    result.Add(pi.Name, contentArea.Items.Select(i => MapContent(i.GetContent())).ToList());
                }
                else if (pi.Value is string[])
                {
                    result.Add(pi.Name, (pi.Value as string[]));
                }
                else if (pi.Value is Int32 || pi.Value is Boolean || pi.Value is DateTime || pi.Value is Double)
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

            if(newProperties.ContainsKey("__EpiserverMoveEntityTo"))
            {
                try
                {
                    var moveTo = FindContentReference((string)newProperties["__EpiserverMoveEntityTo"]);
                    _repo.Move(contentRef, moveTo);
                }
                catch(ContentNotFoundException)
                {
                    return BadRequest("target page not found");
                }
                newProperties.Remove("__EpiserverMoveEntityTo");
            }

            if(newProperties.ContainsKey("Name"))
            {
                content.Name = newProperties["Name"].ToString();
                newProperties.Remove("Name");
            }
            
            // Store the new information in the object.
            var error = UpdateContentWithProperties(newProperties, content);
            if (!string.IsNullOrEmpty(error)) return BadRequest($"Invalid property '{error}'");

            // Save the reference and publish if requested.
            var updatedReference = _repo.Save(content, saveaction);
            return Ok(new { reference = updatedReference.ToString() });
        }

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpPost, Route("entity/{*path}")]
        public virtual IHttpActionResult CreateContent(string path, [FromBody] Dictionary<string,object> contentProperties, EPiServer.DataAccess.SaveAction action = EPiServer.DataAccess.SaveAction.Save)
        {
            path = path ?? "";
            var parentContentRef = FindContentReference(path);
            if (parentContentRef == ContentReference.EmptyReference) return NotFound();

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

            var pageName = (string)nameValue;

            if (_repo.GetChildren<IContent>(parentContentRef).Any(ch => ch.Name == pageName))
            {
                return BadRequest($"Content with name '{pageName}' already exists");
            }

            EPiServer.DataAccess.SaveAction saveaction = action;
            if (contentProperties.ContainsKey("SaveAction") && (string)contentProperties["SaveAction"] == "Publish")
            {
                saveaction = EPiServer.DataAccess.SaveAction.Publish;
                contentProperties.Remove("SaveAction");
            }
            
            // Create content.
            IContent content = _repo.GetDefault<IContent>(parentContentRef, contentType.ID);

            content.Name = pageName;

            // Set all the other values.
            var error = UpdateContentWithProperties(contentProperties, content);
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
            
            _repo.MoveToWastebasket(contentReference);
            return Ok();
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("binary/{*path}")]
        public virtual IHttpActionResult GetBinaryContent(string path)
        {
            path = path ?? "";
            var contentRef = FindContentReference(path);
            if (contentRef == ContentReference.EmptyReference) return NotFound();
                
            var content = _repo.Get<IContent>(contentRef);

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

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("children/{*path}")]
        public virtual IHttpActionResult GetChildren(string path)
        {
            path = path ?? "";
            var contentReference = FindContentReference(path);
            if (contentReference == ContentReference.EmptyReference ||
                !_repo.TryGet(contentReference, out IContent parentContent)) return NotFound();

            var children = new List<Dictionary<string, object>>();

            // Collect sub pages
            children.AddRange(_repo.GetChildren<IContent>(contentReference).Select(x => MapContent(x)));

            if (parentContent is PageData)
            {
                // Collect Main Content
                var main = (parentContent.Property.Get("MainContentArea")?.Value as ContentArea);
                if (main != null)
                    children.AddRange(main.Items.Select(x => MapContent(_repo.Get<IContent>(x.ContentLink))));
                
                // Collect Related Content
                var related = (parentContent.Property.Get("RelatedContentArea")?.Value as ContentArea);
                if (related != null)
                    children.AddRange(related.Items.Select(x => MapContent(_repo.Get<IContent>(x.ContentLink))));
            }

            return Ok(children.ToArray());
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("entity/{*Path}")]
        public virtual IHttpActionResult GetEntity(string Path)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference) return NotFound();

            try
            {
                var content = _repo.Get<IContent>(r);
                if (content.IsDeleted) return NotFound();
                return Ok(MapContent(content));
            }
            catch(ContentNotFoundException)
            {
                return NotFound();
            }
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("type-by-entity/{Path}")]
        public virtual IHttpActionResult GetContentTypeByPath(string Path)
        {
            Path = Path ?? "";

            var reference = FindContentReference(Path);

            if(reference.Equals(ContentReference.EmptyReference))
            {
                return NotFound();
            }

            if(!_repo.TryGet(reference, out IContent content))
            {
                return NotFound();
            }

            //var page = _repo.GetDefault<IContent>(ContentReference.RootPage, reference.ID);
            //we don't use episerverType.PropertyDefinitions since those don't include everything (PageCreated for example)

            return new JsonResult<object>(new
            {
                TypeName = content.GetOriginalType().Name,
                Properties = content.Property.Select(p => new { Name = p.Name, Type = p.Type.ToString() })
            },
                new JsonSerializerSettings(), Encoding.UTF8, this);
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
            //we don't use episerverType.PropertyDefinitions since those don't include everything (PageCreated for example)

            return new JsonResult<object>(new
                {
                    TypeName = Type,
                    Properties = page.Property.Select(p => new { Name = p.Name, Type = p.Type.ToString() })
                },
                new JsonSerializerSettings(), Encoding.UTF8, this);
        }
        
        private void WriteBlobToStorage(string name, byte[] data, MediaData md)
        {
            var blob = _blobfactory.CreateBlob(md.BinaryDataContainer, Path.GetExtension(name));
            using (var s = blob.OpenWrite())
            {
                BinaryWriter w = new BinaryWriter(s);
                w.Write(data);
                w.Flush();
            }
            md.BinaryData = blob;
        }

        private string UpdateContentWithProperties(IDictionary<string, object> properties, IContent content)
        {
            foreach (var propertyName in properties.Keys)
            {
                var errorMessage = UpdateFieldOnContent(properties, content, propertyName);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    return errorMessage;
                }
            }
            return null;
        }

        private string UpdateFieldOnContent(IDictionary<string, object> properties, IContent con, string propertyName)
        {
            //Problem: con might only contain very few properties (not inherited)
            if (con.Property.Contains(propertyName))
            {

                if (con.Property[propertyName] is EPiServer.SpecializedProperties.PropertyContentArea)
                {
                    //Handle if property is Content Area.
                    if (con.Property[propertyName].Value == null) con.Property[propertyName].Value = new ContentArea();
                    ContentArea ca = con.Property[propertyName].Value as ContentArea;
                    var lst = properties[propertyName];
                    if (lst is List<object>)
                    {
                        foreach (var s in (lst as List<object>))
                        {
                            var itmref = FindContentReferenceByString(s.ToString());
                            ca.Items.Add(new ContentAreaItem() { ContentLink = itmref });
                        }
                    }
                }
                else if (properties[propertyName] is string[])
                {
                    con.Property[propertyName].Value = properties[propertyName] as string[];
                }
                else if (con.Property[propertyName].GetType() == typeof(EPiServer.Core.PropertyDate))
                {
                    if (properties[propertyName] is DateTime)
                    {
                        con.Property[propertyName].Value = properties[propertyName];
                    }
                    else
                    {
                        con.Property[propertyName].ParseToSelf((string)properties[propertyName]);
                    }
                }
                else
                {
                    con.Property[propertyName].Value = properties[propertyName];
                }
                return null;
            }

            if (propertyName.ToLower() == "binarydata" && con is MediaData)
            {
                dynamic binitm = properties[propertyName];
                byte[] bytes = Convert.FromBase64String(binitm);
                WriteBlobToStorage(con.Name ?? (string)properties["Name"], bytes, con as MediaData);
                return null;
            }

            return propertyName;
        }

        /// <summary>
        /// Transforms a name into an URLEncoded name.
        /// </summary>
        /// <param name="name">The origional name</param>
        /// <returns>An URLEncoded name</returns>
        private string SegmentedName(string name)
        {
            return name.Replace(' ', '-').ToLower();
        }

        private ContentReference FindContentReference(string Path)
        {
            if (String.IsNullOrEmpty(Path))
            {
                return ContentReference.RootPage;
            }

            var parts = Path.Split(new char[1] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var r = FindContentReferenceByString(parts.First());
            foreach (var k in parts.Skip(1))
            {
                r = LookupRef(r, k);
            }

            return r;
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