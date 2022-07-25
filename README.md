<div id="top"></div>

<!-- PROJECT SHIELDS -->
<!--
*** I'm using markdown "reference style" links for readability.
*** Reference links are enclosed in brackets [ ] instead of parentheses ( ).
*** See the bottom of this document for the declaration of the reference variables
*** for contributors-url, forks-url, etc. This is an optional, concise syntax you may use.
*** https://www.markdownguide.org/basic-syntax/#reference-style-links
-->

[![Contributors][contributors-shield]][contributors-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![MIT License][license-shield]][license-url]


<!-- PROJECT LOGO -->
<br />
<div align="center">
  <a href="https://github.com/iyyel/fio">
    <img src="images/fio_logo_wide.png" width="auto" height="300" alt="FIO Logo">
  </a>

  <!-- <h3 align="center">Title</h3> -->

  <p align="center">
    <br />
    :wrench: A type-safe, highly concurrent and asynchronous library for F# based on pure functional programming
    <br />
    <!--
    <a href="https://github.com/othneildrew/Best-README-Template"><strong>Explore the docs »</strong></a>
    <br />
    <br />
    <a href="https://github.com/othneildrew/Best-README-Template">View Demo</a>
    ·
    <a href="https://github.com/othneildrew/Best-README-Template/issues">Report Bug</a>
    ·
    <a href="https://github.com/othneildrew/Best-README-Template/issues">Request Feature</a>
    -->
  </p>
</div>



<!-- TABLE OF CONTENTS -->
<details>
  <summary>Table of Contents</summary>
  <ol>
    <li>
      <a href="#about-the-project">About FIO</a>
      <ul>
        <li><a href="#built-with">Built With</a></li>
      </ul>
    </li>
    <li>
      <a href="#getting-started">Getting Started</a>
      <ul>
        <li><a href="#prerequisites">Prerequisites</a></li>
        <li><a href="#installation">Installation</a></li>
      </ul>
    </li>
    <li>
      <a href="#usage">Usage</a>
      <ul>
        <li><a href="#benchmarks">Benchmarks</a></li>
      </ul>
    </li>
    <li><a href="#performance">Performance</a></li>
    <li><a href="#license">License</a></li>
    <li><a href="#contact">Contact</a></li>
    <li><a href="#acknowledgments">Acknowledgments</a></li>
  </ol>
</details>



<!-- ABOUT THE PROJECT -->
## About The Project

<!-- [![FIO][product-screenshot]](https://github.com/iyyel/fio) -->

**FIO** is a type-safe, highly concurrent and asynchronous library for F# that is based on principles from pure functional programming. It provides a construct known as the IO monad for handling expressions with side effects. It uses the concept of "green threads" also known as "fibers" to provide scalable and efficient concurrency.

**FIO** is an attempt at creating a similar environment to that of [ZIO](https://zio.dev/) for Scala. **FIO** is both inspired by
[ZIO](https://zio.dev/) and [Cats Effect](https://typelevel.org/cats-effect/).

**DISCLAIMER:** **FIO** is in early development stages and a lot of improvements and enhancements can be made. Expect bugs.

<p align="right">(<a href="#top">back to top</a>)</p>



### Built With

**FIO** is built using the following technologies:

* [F#](https://fsharp.org/)
* [.NET 6.0](https://docs.microsoft.com/en-us/dotnet/core/whats-new/dotnet-6)

<p align="right">(<a href="#top">back to top</a>)</p>



<!-- GETTING STARTED -->
## Getting Started

It is easy to get started with **FIO**.

### Prerequisites

* Download and install [.NET 6.0](https://dotnet.microsoft.com/en-us/download/dotnet/6.0)
* Download and install a compatible IDE such as [Visual Studio](https://visualstudio.microsoft.com/downloads/) or [Rider](https://www.jetbrains.com/rider/download/)

### Installation

* Download or clone this repository
* Open it in your IDE of choice
* Navigate to the _Examples_ project and check out the example programs or create a new file to start using **FIO**

<p align="right">(<a href="#top">back to top</a>)</p>



## Usage

Create a new class and import the library using "open FSharp.FIO". For example:

```fsharp
open FSharp.FIO

[<EntryPoint>]
let main _ =
  let askForName =
    fio (fun () -> printfn "%s" "Hello! What is your name?")
    >> fun _ ->
    fio (fun () -> Console.ReadLine())
    >> fun name ->
    fio (fun () -> printfn $"Hello, %s{name}, welcome to FIO!")

  let fiber = Advanced.Runtime().Run askForName
  let result = fiber.Await()
  printfn $"%A{result}"
```

<p align="right">(<a href="#top">back to top</a>)</p>



## Benchmarks

This repository contains five benchmarks that each tests an aspect of concurrent computing.
All benchmarks reside from the [Savina - An Actor Benchmark Suite](http://soft.vub.ac.be/AGERE14/papers/ageresplash2014_submission_19.pdf) paper.

* Pingpong (Message sending and retrieval)
* ThreadRing (Message sending and retrieval, context switching between fibers)
* Big (Contention on channel, many-to-many message passing)
* Bang (Many-to-one messaging)
* Spawn (Spawning time of fibers)

The benchmarks can be through the following command line options:

```
OPTIONS:

    --naive-runtime       specify naive runtime. (specify only one runtime)
    --intermediate-runtime <evalworkercount> <blockingworkercount> <evalstepcount>
                          specify eval worker count, blocking worker count and eval step count for intermediate
                          runtime. (specify only one runtime)
    --advanced-runtime <evalworkercount> <blockingworkercount> <evalstepcount>
                          specify eval worker count, blocking worker count and eval step count for advanced runtime.
                          (specify only one runtime)
    --deadlocking-runtime <evalworkercount> <blockingworkercount> <evalstepcount>
                          specify eval worker count, blocking worker count and eval step count for deadlocking
                          runtime. (specify only one runtime)
    --runs <runs>         specify the number of runs for each benchmark.
    --process-increment <processcountinc> <inctimes>
                          specify the value of process count increment and how many times.
    --pingpong <roundcount>
                          specify round count for pingpong benchmark.
    --threadring <processcount> <roundcount>
                          specify process count and round count for threadring benchmark.
    --big <processcount> <roundcount>
                          specify process count and round count for big benchmark.
    --bang <processcount> <roundcount>
                          specify process count and round count for bang benchmark.
    --spawn <processcount>
                          specify process count for spawn benchmark.
    --help                display this list of options.           display this list of options.
```

For example, running 30 runs of each benchmark using the advanced runtime with 7 evaluation workers, 1 blocking worker and 15 evaluation steps would look as so:

```
--advanced-runtime 7 1 15 --runs 30 --pingpong 120000 --threadring 2000 1 --big 500 1 --bang 3000 1 --spawn 3000
```

Additionally, the **FIO** project supports two conditional compilation options:

* **DETECT_DEADLOCK:** Enables a naive deadlock detecting thread that attempts to detect if a deadlock has occurred when running FIO programs
* **MONITOR:** Enables a monitoring thread that prints out data structure content during when running FIO programs

<p align="right">(<a href="#top">back to top</a>)</p>


<!-- PERFORMANCE -->
## Performance


<p align="right">(<a href="#top">back to top</a>)</p>


<!-- LICENSE -->
## License

Distributed under the GNU General Public License v3.0. See [LICENSE.md](LICENSE.md) for more information.

<p align="right">(<a href="#top">back to top</a>)</p>



<!-- CONTACT -->
## Contact

Daniel Larsen (iyyel) - [iyyel.io](https://iyyel.io) - [mail@iyyel.io](mailto:mail@iyyel.io)

<p align="right">(<a href="#top">back to top</a>)</p>



<!-- ACKNOWLEDGMENTS -->
## Acknowledgments

* Alceste Scalas - [alcsc](https://people.compute.dtu.dk/alcsc/) - [github](https://github.com/alcestes)

<p align="right">(<a href="#top">back to top</a>)</p>



<!-- MARKDOWN LINKS & IMAGES -->
<!-- https://www.markdownguide.org/basic-syntax/#reference-style-links -->
[contributors-shield]: https://img.shields.io/github/contributors/iyyel/fio.svg?style=for-the-badge
[contributors-url]: https://github.com/iyyel/fio/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/iyyel/fio.svg?style=for-the-badge
[forks-url]: https://github.com/iyyel/fio/network/members
[stars-shield]: https://img.shields.io/github/stars/iyyel/fio.svg?style=for-the-badge
[stars-url]: https://github.com/iyyel/fio/stargazers
[issues-shield]: https://img.shields.io/github/issues/iyyel/fio.svg?style=for-the-badge
[issues-url]: https://github.com/iyyel/fio/issues
[license-shield]: https://img.shields.io/github/license/iyyel/fio.svg?style=for-the-badge
[license-url]: https://github.com/iyyel/fio/blob/main/LICENSE.md
<!-- [linkedin-shield]: https://img.shields.io/badge/-LinkedIn-black.svg?style=for-the-badge&logo=linkedin&colorB=555
[linkedin-url]: https://linkedin.com/in/ 
[product-screenshot]: images/main_menu.png
-->
