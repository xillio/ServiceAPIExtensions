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
        
        public static Dictionary<string, object> ConstructExpandoObject(IContent c)
        {
            if (c == null)
            {
                return null;
            }
            var result = new Dictionary<string, object>();
            
            result["Name"] = c.Name;
            result["ParentLink"] = c.ParentLink;
            result["ContentGuid"] = c.ContentGuid;
            result["ContentLink"] = c.ContentLink;
            result["ContentTypeID"] = c.ContentTypeID;
            result["__EpiserverContentType"] = GetContentType(c);

            var content = c as IBinaryStorable;

            if (content!=null)
            {
                // IF the content has binarydata, get the Hash and size.

                if (content.BinaryData != null)
                {
                    using (Stream stream = content.BinaryData.OpenRead())
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

                if (c is MediaData)
                    result.Add("MimeType", (c as MediaData).MimeType);
            }
            
            foreach (var pi in c.Property)
            {
                if (pi.Value != null)
                {
                    if (pi.Type == PropertyDataType.Block)
                    {
                        //TODO: Doesn't work. Check SiteLogoType on start page
                        if(pi.Value is IContent)  result.Add(pi.Name, ConstructExpandoObject((IContent)pi.Value));
                    }
                    else if (pi is EPiServer.SpecializedProperties.PropertyContentArea)
                    {
                        //TODO: Loop through and make array
                        var pca = pi as EPiServer.SpecializedProperties.PropertyContentArea;
                        ContentArea ca = pca.Value as ContentArea;
                        var lst=new List<Dictionary<string,object>>();
                        foreach(var itm in ca.Items){
                            var itmobj = ConstructExpandoObject(itm.GetContent());
                            lst.Add(itmobj);
                        }
                        result.Add(pi.Name, lst.ToArray());

                    } 
                    else if (pi.Value is string[])
                    {
                        result.Add(pi.Name, (pi.Value as string[]));
                    }
                    else if (pi.Value is Int32  || pi.Value is Boolean || pi.Value is DateTime || pi.Value is Double)
                    {
                        result.Add(pi.Name, pi.Value);
                    }
                    else { 
                        //TODO: Handle different return values
                        result.Add(pi.Name, (pi.Value != null) ? pi.ToWebString() : null);
                    }
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

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpPut, Route("entity/{*Path}")]
        public virtual IHttpActionResult UpdateContent(string Path, [FromBody] Dictionary<string,object> Updated, EPiServer.DataAccess.SaveAction action = EPiServer.DataAccess.SaveAction.Save)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference) return NotFound();
            if (r == ContentReference.RootPage) return BadRequest("Cannot update Root entity");

            if(!_repo.TryGet(r, out IContent content))
            {
                return NotFound();
            }

            content = (content as IReadOnly).CreateWritableClone() as IContent;
            var dic = Updated as IDictionary<string, object>;
            EPiServer.DataAccess.SaveAction saveaction = action;
            if (dic.ContainsKey("SaveAction") && ((string)dic["SaveAction"]) == "Publish")
            {
                saveaction = EPiServer.DataAccess.SaveAction.Publish;
                dic.Remove("SaveAction");
            }

            if(dic.ContainsKey("__EpiserverMoveEntityTo"))
            {
                try
                {
                    var moveTo = FindContentReference((string)dic["__EpiserverMoveEntityTo"]);
                    _repo.Move(r, moveTo);
                }
                catch(ContentNotFoundException)
                {
                    return BadRequest("target page not found");
                }
                dic.Remove("__EpiserverMoveEntityTo");
            }

            if(dic.ContainsKey("Name"))
            {
                content.Name = dic["Name"].ToString();
                dic.Remove("Name");
            }
            
            // Store the new information in the object.
            UpdateContentWithProperties(dic, content, out string error);
            if (!string.IsNullOrEmpty(error)) return BadRequest($"Invalid property '{error}'");

            // Save the reference and publish if requested.
            var rt = _repo.Save(content, saveaction);
            return Ok(new { reference = rt.ToString() });
        }

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpPost, Route("entity/{*Path}")]
        public virtual IHttpActionResult CreateContent(string Path, [FromBody] ExpandoObject content, EPiServer.DataAccess.SaveAction action = EPiServer.DataAccess.SaveAction.Save)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference) return NotFound();

            // Instantiate content of named type.
            var properties = content as IDictionary<string, object>;
            if (properties == null || !properties.TryGetValue("ContentType", out object ContentType))
                return BadRequest("'ContentType' is a required field.");

            // Check ContentType.
            var ctype = _typerepo.Load((string)ContentType);
            if (ctype == null && int.TryParse((string)ContentType, out int j)) ctype = _typerepo.Load(j);
            if (ctype == null) return BadRequest($"'{ContentType}' is an invalid ContentType");

            // Remove 'ContentType' from properties before iterating properties.
            properties.Remove("ContentType");

            // Check if the object already exists.
            if (properties.TryGetValue("Name", out object name))
            {
                var temp = _repo.GetChildren<IContent>(r).Where(ch => ch.Name == (string)name).FirstOrDefault();
                if (temp != null) return BadRequest($"Content with name '{name}' already exists");
            }
            
            // Create content.
            IContent con = _repo.GetDefault<IContent>(r, ctype.ID);

            EPiServer.DataAccess.SaveAction saveaction = action;
            if (properties.ContainsKey("SaveAction") && (string)properties["SaveAction"] == "Publish")
            {
                saveaction = EPiServer.DataAccess.SaveAction.Publish;
                properties.Remove("SaveAction");
            }

            // Set the reference name.
            string _name = "";
            if (properties.ContainsKey("Name"))
            {
                _name = properties["Name"].ToString();
                properties.Remove("Name");
            }
            
            if (!string.IsNullOrEmpty(_name)) con.Name = _name;

            // Set all the other values.
            UpdateContentWithProperties(properties, con, out string error);
            if (!string.IsNullOrEmpty(error)) return BadRequest($"Invalid property '{error}'");

            // Save the reference with the requested save action.
            if (!string.IsNullOrEmpty(_name)) con.Name = _name;
            try
            {
                var rt = _repo.Save(con, saveaction);
                return Created<object>(Path, new { reference = rt.ID });
            } catch (ValidationException ex)
            {
                return BadRequest(ex.Message);
            }
        }
        
        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpDelete, Route("entity/{*Path}")]
        public virtual IHttpActionResult DeleteContent(string Path)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference) return NotFound();
            if (r == ContentReference.RootPage && string.IsNullOrEmpty(Path)) return BadRequest("'root' can only be deleted by specifying its name in the path!");

            // If its already in the wastebasket delete it, otherwise put it in the wastebasket.
            _repo.MoveToWastebasket(r);
            return Ok();
        }

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("binary/{*Path}")]
        public virtual IHttpActionResult GetBinaryContent(string Path)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference) return NotFound();
                
            var cnt = _repo.Get<IContent>(r);

            if (cnt is IBinaryStorable)
            {
                var binary = cnt as IBinaryStorable;
                if (binary.BinaryData == null) return NotFound();

                // Return the binary contents as a stream.
                using (var br = new BinaryReader(binary.BinaryData.OpenRead()))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Content = new ByteArrayContent(br.ReadBytes((int)br.BaseStream.Length));
                    if (cnt as IContentMedia != null)
                        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue((cnt as IContentMedia).MimeType);
                    else
                    {
                        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    }
                    return ResponseMessage(response);
                }
            }

            if (cnt is PageData)
            {
                var page = cnt as PageData;

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

        [AuthorizePermission("EPiServerServiceApi", "ReadAccess"), HttpGet, Route("children/{*Path}")]
        public virtual IHttpActionResult GetChildren(string Path)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference ||
                !_repo.TryGet<IContent>(r, out IContent parent)) return NotFound();

            var children = new List<Dictionary<string, object>>();

            // Collect sub pages
            children.AddRange(_repo.GetChildren<IContent>(r).Select(x => ConstructExpandoObject(x)));

            if (parent is PageData)
            {
                // Collect Main Content
                var main = (parent.Property.Get("MainContentArea")?.Value as ContentArea);
                if (main != null)
                    children.AddRange(main.Items.Select(x => ConstructExpandoObject(_repo.Get<IContent>(x.ContentLink))));
                
                // Collect Related Content
                var related = (parent.Property.Get("RelatedContentArea")?.Value as ContentArea);
                if (related != null)
                    children.AddRange(related.Items.Select(x => ConstructExpandoObject(_repo.Get<IContent>(x.ContentLink))));
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
                return Ok(ConstructExpandoObject(content));
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

        private void UpdateContentWithProperties(IDictionary<string, object> properties, IContent con, out string error)
        {
            error = "";
            foreach (var k in properties.Keys)
            {
                UpdateFieldOnContent(properties, con, k, out error);
                if (!string.IsNullOrEmpty(error)) return;
            }
        }

        private void UpdateFieldOnContent(IDictionary<string, object> properties, IContent con, string k, out string error)
        {
            error = "";
            //Problem: con might only contain very few properties (not inherited)
            if (con.Property.Contains(k))
            {

                if (con.Property[k] is EPiServer.SpecializedProperties.PropertyContentArea)
                {
                    //Handle if property is Content Area.
                    if (con.Property[k].Value == null) con.Property[k].Value = new ContentArea();
                    ContentArea ca = con.Property[k].Value as ContentArea;
                    var lst = properties[k];
                    if (lst is List<object>)
                    {
                        foreach (var s in (lst as List<object>))
                        {
                            var itmref = FindContentReferenceByString(s.ToString());
                            ca.Items.Add(new ContentAreaItem() { ContentLink = itmref });
                        }
                    }
                }
                else if (properties[k] is string[])
                {
                    con.Property[k].Value = properties[k] as string[];
                }
                else if (con.Property[k].GetType() == typeof(EPiServer.Core.PropertyDate))
                {
                    if (properties[k] is DateTime)
                    {
                        con.Property[k].Value = properties[k];
                    }
                    else
                    {
                        con.Property[k].ParseToSelf((string)properties[k]);
                    }
                }
                else
                {
                    con.Property[k].Value = properties[k];
                }
            }
            else if (k.ToLower() == "binarydata" && con is MediaData)
            {
                dynamic binitm = properties[k];
                byte[] bytes = Convert.FromBase64String(binitm);
                WriteBlobToStorage(con.Name ?? (string)properties["Name"], bytes, con as MediaData);
            } else
            {
                error = k;
                return;
            }
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