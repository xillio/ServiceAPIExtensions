using EPiServer;
using EPiServer.Core;
using EPiServer.Core.Transfer;
using EPiServer.DataAbstraction;
using EPiServer.ServiceApi.Models;
using EPiServer.ServiceLocation;
using EPiServer.SpecializedProperties;
using Newtonsoft.Json.Linq;
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
using EPiServer.Web.Routing;
using EPiServer.Data.Entity;
using EPiServer.Web.Internal;
using System.Reflection;
using Newtonsoft.Json;
using System.Text;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;

namespace ServiceAPIExtensions.Controllers
{
    [/*AuthorizePermission("EPiServerServiceApi", "WriteAccess"),*/ RequireHttps, RoutePrefix("episerverapi/content")]
    public class ContentAPiController : ApiController
    {
        protected IContentRepository _repo = ServiceLocator.Current.GetInstance<IContentRepository>();
        protected IContentTypeRepository _typerepo = ServiceLocator.Current.GetInstance<IContentTypeRepository>();
        protected IRawContentRetriever _rc = ServiceLocator.Current.GetInstance<IRawContentRetriever>();
        protected BlobFactory _blobfactory = ServiceLocator.Current.GetInstance<BlobFactory>();
        
        /// <summary>
        /// Finds the content with the given name
        /// </summary>
        /// <param name="Ref">The name of the content</param>
        /// <returns>The requested content on success or ContentReference.EmptyReference otherwise</returns>
        protected ContentReference LookupRef(string Ref)
        {
            if (Ref == "") return ContentReference.RootPage;
            if (Ref.ToLower() == "root") return ContentReference.RootPage;
            if (Ref.ToLower() == "start") return ContentReference.StartPage;
            if (Ref.ToLower() == "globalblock") return ContentReference.GlobalBlockFolder;
            if (Ref.ToLower() == "siteblock") return ContentReference.SiteBlockFolder;
            
            if (ContentReference.TryParse(Ref, out ContentReference c)) return c;

            Guid g=Guid.Empty;
            if (Guid.TryParse(Ref, out g)) EPiServer.Web.PermanentLinkUtility.FindContentReference(g);
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

        /// <summary>
        /// Finds the content with a given name and its type.  Favours URLEncoded name over actual name.
        /// </summary>
        /// <param name="Parent">The reference to the parent</param>
        /// <param name="ContentType">The content type</param>
        /// <param name="Name">The name of the content</param>
        /// <returns>The requested content on success or ContentReference.EmptyReference otherwise</returns>
        protected ContentReference LookupRef(ContentReference Parent, string ContentType, string Name)
        {
            var content = (new UrlSegment(_repo)).GetContentBySegment(Parent, Name);
            if (content != null) return content;
            else
            {
                var temp = _repo.GetChildren<IContent>(Parent).Where(ch => ch.GetType().Name == ContentType && ch.Name == Name).FirstOrDefault();
                if (temp != null) return temp.ContentLink;
                else return ContentReference.EmptyReference;
            }
        }
        
        public static ExpandoObject ConstructExpandoObject(IContent c, string Select=null)
        {
            return ConstructExpandoObject(c,true, Select);
        }

        public static ExpandoObject ConstructExpandoObject(IContent c, bool IncludeBinary,string Select=null)
        {
            dynamic e = new ExpandoObject();
            var dic=e as IDictionary<string,object>;

            if (c == null) return null;

            e.Name = c.Name;
            e.ParentLink = c.ParentLink;
            e.ContentGuid = c.ContentGuid;
            e.ContentLink = c.ContentLink;
            e.ContentTypeID = c.ContentTypeID;
            //TODO: Resolve Content Type
            var parts = (Select == null) ? null : Select.Split(',');

            var content = c as IBinaryStorable;

            if (content!=null)
            {
                // IF the content has binarydata, get the Hash and size.

                if (content.BinaryData != null)
                {
                    using (Stream stream = content.BinaryData.OpenRead())
                    {
                        dic.Add("MD5", Hash(stream, MD5.Create()));
                        dic.Add("SHA1", Hash(stream, SHA1.Create()));
                        dic.Add("SHA256", Hash(stream, SHA256.Create()));
                        dic.Add("FileSize", stream.Length);
                    }
                }
                else
                {
                    dic.Add("FileSize", 0);
                }

                if (c is MediaData)
                    dic.Add("MimeType", (c as MediaData).MimeType);
            }
            
            foreach (var pi in c.Property)
            {
                if (parts != null && (!parts.Contains(pi.Name))) continue;

                if (pi.Value != null)
                {
                    if (pi.Type == PropertyDataType.Block)
                    {
                        //TODO: Doesn't work. Check SiteLogoType on start page
                        if(pi.Value is IContent)  dic.Add(pi.Name, ConstructExpandoObject((IContent)pi.Value));
                    }
                    else if (pi is EPiServer.SpecializedProperties.PropertyContentArea)
                    {
                        //TODO: Loop through and make array
                        var pca = pi as EPiServer.SpecializedProperties.PropertyContentArea;
                        ContentArea ca = pca.Value as ContentArea;
                        List<ExpandoObject> lst=new List<ExpandoObject>();
                        foreach(var itm in ca.Items){
                            dynamic itmobj = ConstructExpandoObject(itm.GetContent());
                            lst.Add(itmobj);
                        }
                        dic.Add(pi.Name, lst.ToArray());

                    } 
                    else if (pi.Value is string[])
                    {
                        dic.Add(pi.Name, (pi.Value as string[]));
                    }
                    else if (pi.Value is Int32  || pi.Value is Boolean || pi.Value is DateTime || pi.Value is Double)
                    {
                        dic.Add(pi.Name, pi.Value);
                    }
                    else { 
                        //TODO: Handle different return values
                        dic.Add(pi.Name, (pi.Value != null) ? pi.ToWebString() : null);
                    }
                }
                    
            }
            return e;
        }

        [AuthorizePermission("EPiServerServiceApi", "WriteAccess"), HttpPut, Route("entity/{*Path}")]
        public virtual IHttpActionResult UpdateContent(string Path, [FromBody] ExpandoObject Updated, EPiServer.DataAccess.SaveAction action = EPiServer.DataAccess.SaveAction.Save)
        {
            Path = Path ?? "";
            var r = FindContentReference(Path);
            if (r == ContentReference.EmptyReference) return NotFound();
            if (r == ContentReference.RootPage) return BadRequest("Cannot update Root entity");

            var content = (_repo.Get<IContent>(r) as IReadOnly).CreateWritableClone() as IContent;
            var dic = Updated as IDictionary<string, object>;
            EPiServer.DataAccess.SaveAction saveaction = action;
            if (dic.ContainsKey("SaveAction") && ((string)dic["SaveAction"]) == "Publish")
            {
                saveaction = EPiServer.DataAccess.SaveAction.Publish;
                dic.Remove("SaveAction");
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
            else _repo.MoveToWastebasket(r);
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

            List<ExpandoObject> children = new List<ExpandoObject>();

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

            var content = _repo.Get<IContent>(r);
            if (content.IsDeleted) return NotFound();
            return Ok(ConstructExpandoObject(content));
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
                            var itmref = LookupRef(s.ToString());
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
            var r = LookupRef(parts.First());
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