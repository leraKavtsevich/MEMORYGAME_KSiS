using Microsoft.AspNetCore.Mvc;

namespace MemoryGame.Controllers
{
    public class HomeController : Controller
    {
        

        public IActionResult Index()
        {
            return View();
        }

 
       
    }
}
