# Htmx with Asp.Net Core

<datetime class="hidden">2024-08-01T03:42</datetime>

<!--category-- ASP.NET, HTMX -->

## Introduction

Using HTMX with ASP.NET Core is a great way to build dynamic web applications with minimal JavaScript. HTMX allows you to update parts of your page without a full page reload, making your application feel more responsive and interactive.

It's what I used to call 'hybrid' web design where you render the page fully using server-side code and then use HTMX to update parts of the page dynamically.

In this article, I'll show you how to get started with HTMX in an ASP.NET Core application.


[TOC]

## Prerequisites

HTMX - Htmx is a JavaScript package the easist way to include it in your project is to use a CDN. (See [here](https://htmx.org/docs/#installing) )

```html
<script src="https://unpkg.com/htmx.org@2.0.1" integrity="sha384-QWGpdj554B4ETpJJC9z+ZHJcA/i59TyjxEPXiiUgN2WmTyV5OEZWCD6gQhgkdpB/" crossorigin="anonymous"></script>
```

You can of course also download a copy and include it 'manually' (or use LibMan or npm).


## ASP.NET Bits

I also recommend installing the Htmx Tag Helper from [here](https://github.com/khalidabuhakmeh/Htmx.Net)


These are both from the wonderful [Khalid Abuhakmeh
](https://mastodon.social/@khalidabuhakmeh@mastodon.social)

``` shell 
dotnet add package Htmx.TagHelpers
```

And the Htmx Nuget package from [here](https://www.nuget.org/packages/Htmx/)


``` shell
 dotnet add package Htmx
 ```

The tag helper lets you do this:

``` razor
    <a hx-controller="Blog" hx-action="Show" hx-push-url="true" hx-get hx-target="#contentcontainer" hx-route-slug="@Model.Slug"
        class="block font-body text-lg font-semibold text-primary transition-colors hover:text-green dark:text-white dark:hover:text-secondary">@Model.Title</a>
```
### Alternative approach.

**NOTE: This approach has one major drawback; it doesn't produce an href for the post link. This is a problem for SEO and accessibility. It also means these links will fail if HTMX for some reason doesn't load (CDNs DO go down).**

An alternative approach is to use the ``` hx-boost="true"``` attribute and normal asp.net core tag helpers. See  [here](https://htmx.org/docs/#hx-boost) for more info on hx-boost (though the docs are a bit sparse). 
This will output a normal href but will be intercepted by HTMX and the content loaded dynamically.

So as follows:

```razor
 <a asp-controller="Blog" asp-action="Show" hx-boost="true"  hx-target="#contentcontainer" asp-route-slug="@Model.Slug"
       class="block font-body text-lg font-semibold text-primary transition-colors hover:text-green dark:text-white dark:hover:text-secondary">@Model.Title</a>
```





### Partials

HTMX works well with partial views. You can use HTMX to load a partial view into a container on your page. This is great for loading parts of your page dynamically without a full page reload.

In this app we have a container in the Layout.cshtml file that we want to load a partial view into.

```razor
    <div class="container mx-auto" id="contentcontainer">
   @RenderBody()

    </div>
```
Normally it renders the server side content but using the HTMX tag helper about you can see we target ``` hx-target="#contentcontainer" ``` which will load the partial view into the container.

In our project we have the BlogView partial view that we want to load into the container.

![img.png](project.png)

Then in the Blog Controller we have 

```csharp
    [Route("{slug}")]
    [OutputCache(Duration = 3600)]
    public IActionResult Show(string slug)
    {
       var post =  blogService.GetPost(slug);
       if(Request.IsHtmx())
       {
              return PartialView("_PostPartial", post);
       }
       return View("Post", post);
    }
```

You can see here we have the HTMX Request.IsHtmx() method, this will return true if the request is an HTMX request. If it is we return the partial view, if not we return the full view.

Using this we can ensure that we also support direct querying with little real effort.

In this case our full view refers to this partial:
    
```razor
@model Mostlylucid.Models.Blog.BlogPostViewModel

@{
ViewBag.Title = "title";
Layout = "_Layout";
}
<partial name="_PostPartial" model="Model"/>
```

And so we now have a super simple way to load partial views into our page using HTMX.