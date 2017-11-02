# ServiceAPIExtensions
An extension pack for the EPiServer ServiceAPI

Can be installed through Nuget on any EPiServer CMS 9.x site with the ServiceAPI installed.
Currently adds some ContentAPI and some ContentTypeAPI extensions, enabling you to do most CRUD operations on IContent as 
well as fetch meta data on Content Types.
Useful for integrations and migrations.

Currently supported syntax: 

GET /episerverapi/content/{reference}
https://episerverapi.azurewebsites.net/episerverapi/content/start
Get's Content at Reference. Reference can be guid, ID, 'root','start','globalblock','siteblock'.
Returns {"property":"value", ...}
Supports query parameter: Select={List of property names}

PUT /episerverapi/content/{reference}
Updates the content item with the body object content.

POST /episerverapi/content/{reference}/Publish
Publishes the content

GET /episerverapi/content/{reference}/{Property name}
Returns the string representation of that property

GET /episerverapi/content/{reference}/children
https://episerverapi.azurewebsites.net/episerverapi/content/start/children
Get's children of content. Returns {"Children":[{"property":"value", ...}, {}]}
Supports Query parameters: Skip and Take (for pagination), and Select={list of property names}.

POST /episerverapi/content/{parent-reference}/Create/{content-type}/{optional:Save Action}
https://episerverapi.azurewebsites.net/episerverapi/content/start/create/StandardPage
Creates a new content item below the provided parent, of the given type.
The Content Type should be the name of the content type.
The parent-reference should be a reference to an existing content item.
The post body should be resolvable to a Dictionary<string,object>. Whereever a property name matches, it'll assign the value.
If a property "SaveAction" is set to "Publish" it will publish the content right away.
Returns {'reference':'new reference'}.

POST /episerverapi/content/{reference}/upload/{name}
Uploads a blob to a given Media data content item (reference). The Name is the filename being uploaded. 
The POST body should contain the binary data.
Returns void.

GET /episerverapi/content/EnsurePathExist/{content-type}/{*path}
Ensures that the provided path exist - otherwise it'll create it using the assigned container content-type. 
Returns a {reference=last created node}.

GET /episerverapi/content/{reference}/move/{new parent ref}
Moves the content.


Content Type API

GET /episerverapi/contenttype/list
Lists all Content Types

GET /episerverapi/contenttype/{contenttype}
Returns details on that content type

GET /episerverapi/contenttype/typefor/{extension}
Returns the content type that can handle that specific media type.


## Error handling
This extension tries to adhere to standard HTTP status codes, specifically:

* 400: Bad request due to a client error, see below for the structure of errors
* 401: Not authorized: the user doesn't have the necessary permission to an Episerver function. We use `ReadAccess` and `WriteAccess` as defined by the Episerver Service API (https://world.episerver.com/documentation/developer-guides/Episerver-Service-API/). Configure this in the Episerver Administration area -> CMS -> Admin -> Config -> Permissions for Functions -> EPiServerServiceApi.
* 403: Forbidden: the user doesn't have access to an underlying Episerver resource. For example: it's forbidden to get the entity for a page for which you don't have `Read` rights, or move a page to a container for which you don't have `Create` rights.
* 404: Not found. The resource wasn't found.
* 500: An unexpected error occured. Contact the website administrator to resolve this.

### 400 Error structure
The structure for 400 errors is as follows:
```
{
 "errorCode": <the error code>,
 "validationErrors": {
   <fieldname>:
     [{
       "errorCode": <the validation error code>,
       "errorMsg": <a message describing the error>,
       "name": <the fieldname>
      },
      ...]
  ...}
}
```
     
The toplevel `errorCode`s are:

* `BODY_EMPTY`: No body was provided with request.
* `DELETE_ROOT_NOT_ALLOWED`: Deleting the root page in Episerver is not allowed.
* `FIELD_VALIDATION_ERROR`: One or more fields failed to verify.
* `UPDATE_ROOT_NOT_ALLOWED`: Updating the root page in Episerver is not allowed.

The validation `errorCode`s are:

* `CONTENT_TYPE_INVALID`: when creating an entity, the provided `ContentType` was invalid.
* `FIELD_EPISERVER_VALIDATION_ERROR`: an Episerver validation failed. `errorMsg` contains the validation message from Episerver.
* `FIELD_REQUIRED`: this field is required
* `FIELD_INVALID_FORMAT`: the field had an unexpected format.
* `FIELD_INVALID_TYPE`: the provided value for the field had the wrong data type (e.g. providing an integer where a string was required).
* `FIELD_NOT_KNOWN`: a provided field was unexpected for the Episerver `ContentType`.
* `TARGET_CONTAINER_NOT_FOUND`: when moving an entity, the provided target container was not found.
