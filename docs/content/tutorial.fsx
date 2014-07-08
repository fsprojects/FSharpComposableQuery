(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../bin"


(**

Introducing FSharpComposableQuery
=================================

(__work in progress__)

Compositional Query Framework for F# Queries, based on 
[A Practical Theory of Language-Integrated Query (ICFP 2013)](http://dl.acm.org/citation.cfm?id=2500586).  

*)

(**

Referencing the library
-----------------------
*)

#if INTERACTIVE
#r "FSharpComposableQuery.dll"
#r "FSharp.Data.TypeProviders.dll"
#r "System.Data.Linq.dll"
#endif

open FSharpComposableQuery
open Microsoft.FSharp.Data.TypeProviders

(**
All existing F# database and in-memory queries should work as normal. For example:
*)

let data = [1; 5; 7; 11; 18; 21]
let lastNumberInSortedList =
    query {
        for s in data do
        sortBy s
        last
    }

    
(**
In addition, more queries and query compositions work. We illustrate this through several examples below.

Parameterizing a query
----------------------

LINQ already supports queries with parameters, as long as those parameters are of base type. For example, 
to obtain the set of people with age in a given interval, you can create a query parameterized by two integers:

*)
type dbSchema = SqlDataConnection<ConnectionStringName="PeopleConnectionString", ConfigFile=".\\App.config">
let db = dbSchema.GetDataContext()

type People = dbSchema.ServiceTypes.People
type PeopleR = {name:string;age:int}

let range1 = fun (a:int) (b:int) -> 
  query {
    for u in db.People do
    if a <= u.Age && u.Age < b 
    then yield {name=u.Name;age=u.Age}
    } 

let mutable sad = 1

let ex1 = range1 30 40

(**
However, doing it this way is not especially reusable, and we recommend doing it this way instead:
*)

let range = <@ fun (a:int) (b:int) -> 
  query {
    for u in db.People do
    if a <= u.Age && u.Age < b 
    then yield {name=u.Name;age=u.Age}
    }  @>

let ex2 = query { for x in (%range) 30 40 do yield x}


(** The reason is that the first approach only works if the parameters are of base type;
the second is more flexible.  *)

(** 

The query.Run method
---------------------

It's a little awkward to evaluate composite queries due to the fact that we can't splice them directly into 
the query brackets: the following type-checks the arguments, but doesn't do the right thing:

*)

let ex2wrong : System.Linq.IQueryable<PeopleR> = query { (%range) 30 40 }

(**
This happens since the result of (%range) is not explicitly returned in the outer query and gets discarded instead. 

To properly use composite queries, we can instead pass the inner query to the query.Run method as a quotation:
*) 

let ex2correct = query.Run <@ (%range) 30 40 @>

(** 
Or explicitly return the results of the composite query, using the "yield!" keyword in the outer query:
*) 

let ex2correct2 = query { yield! (%range) 30 40 }


(** 

Building queries using query combinators
----------------------------------------

We can also build queries using other query combinators. Suppose we want to find the people who are older than 
a given person and at the same time younger than another. 

The naive way of doing this, using a single parameterized query, would be:
*)

let composeMonolithic = 
    <@ fun s t -> query {
        for u1 in db.People do
        for u2 in db.People do 
        for u in db.People do
            if s = u1.Name && t = u2.Name && u1.Age <= u.Age && u.Age < u2.Age then
                yield {name=u.Name;age=u.Age}
    } @>

(**
We can see this solution is far from perfect: there is code duplication, renaming of variables, and it may be hard
to spot the overall structure of the code.  Moreover, while this query is easy enough to compose in one's head, keeping 
track of the different parameters and constraints becomes more tedious and error-prone as the size and number of tables 
involved in a query grows.  

Compare the previous example to the following one:
*)

let ageFromName = 
  <@ fun s -> query {
        for u in db.People do 
        if s = u.Name then 
          yield u.Age } @>

let compose = 
  <@ fun s t -> query {
      for a in (%ageFromName) s do
      for b in (%ageFromName) t do 
      yield! (%range) a b
  } @>

(**
This way of defining a query exemplifies the logical meaning of the query, and makes it much easier to understand its purpose
from the code. 

The role of the FSharpComposableQuery library in the evaluation of this query is to normalize it to such a form which can then
be evaluated as efficiently as the flat query above. In fact, all composite queries which have an equivalent flat form 
get reduced to it as part of the normalisation procedure.  


Higher-order parameters 
-----------------------

FSharpComposableQuery lifts the restriction that the parameters to a query have to be of base type: instead,
they can be higher-order functions.  
(Actually, F# does handle some higher-order functions, but FSharpComposableQuery provides a stronger guarantee of good behavior.)

For example, we can define the following query combinator that gets all people whose age matches the argument predicate:
*)

let satisfies  = 
 <@ fun p -> query { 
    for u in db.People do
    if p u.Age 
    then yield {name=u.Name;age=u.Age}
   } @>

(**
We can then use it to find all people in their thirties, or all people with even age:
*)

let ex3 = query.Run <@ (%satisfies) (fun x -> 20 <= x && x < 30 ) @>

let ex4 = query.Run <@ (%satisfies) (fun x ->  x % 2 = 0 ) @>

(** 
This is subject to some side-conditions: basically, the function you pass into a higher-order query combinator
may only perform operations that are sensible on the database; recursion and side-effects such as printing are not allowed,
and will result in a run-time error. *)

let wrong1 = query.Run <@ (%satisfies) (fun age -> printfn "%d" age; true) @>

let rec even n = if n = 0 then true
                 else if n = 1 then false
                 else even(n-2)
let wrong2 = query.Run <@ (%satisfies) even @>

(** 
Note that wrong2 is morally equivalent to ex4 above (provided ages are nonnegative), but is not allowed.  The library
is not smart enough to determine that the parameter passed into satisfies is equivalent to an operation that
can be performed on the database (using modular arithmetic); you have to do this yourself.

*)


(** 

Building queries using recursion 
--------------------------------

Although recursion is not allowed *within* a query, you can still use recursion to *build* a query.

Consider the following data type defining some Boolean predicates on ages:

*)

type Predicate = 
  | Above of int
  | Below of int
  | And of Predicate * Predicate
  | Or of Predicate * Predicate
  | Not of Predicate

(** For example, we can define the "in their 30s" predicate two different ways as follows:

*)

let t0 : Predicate = And (Above 30, Below 40)
let t1 : Predicate = Not(Or(Below 30, Above 40))

(** We can define an evaluator that takes a predicate and produces a *parameterized query* as follows:
*)

let rec eval(t) =
  match t with
  | Above n -> <@ fun x -> x >= n @>
  | Below n -> <@ fun x -> x < n @>
  | And (t1,t2) -> <@ fun x -> (%eval t1) x && (%eval t2) x @>
  | Or (t1,t2) -> <@ fun x -> (%eval t1) x || (%eval t2) x @>
  | Not (t0) -> <@ fun x -> not((%eval t0) x) @>

(** Notice that given a predicate t, the return value of this function is a quoted function that takes an integer
and returns a boolean.  Moreover, all of the operations we used are Boolean or arithmetic comparisons that any
database can handle in queries.
*)

(** So, we can plug the predicate obtained from evaluation into the satisfies query combinator, as follows:
*)

let ex6 = query.Run <@ (%satisfies) (%eval t0) @>

let ex7 = query.Run <@ (%satisfies) (%eval t1) @>

(** 
Why is this allowed, even though the eval function is recursive?  
Again, notice that although (%eval t) evaluates recursively to a quotation, all of this happens before it is passed 
as an argument to the satisfies query.  

Had we instead tried it in this, simpler way:
*)

let rec wrongEval t x =
  match t with
  | Above n -> x >= n
  | Below n -> x < n
  | And (t1,t2) -> wrongEval t1 x && wrongEval t2 x
  | Or (t1,t2) -> wrongEval t1 x || wrongEval t2 x 
  | Not (t0) -> not(wrongEval t0 x)

let wrongEx6 = query.Run <@ (%satisfies) (wrongEval t1) @>

(** then we would run into the same problem as before, because we would be trying to run satisfies on quoted
  code containing recursive calls, which is not allowed.  *)