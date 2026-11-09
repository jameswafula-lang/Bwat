/*
 * Bwat Server / QuestionBank.cs
 *
 * The starting math question bank: 20 questions for each level, from
 * Nursery (baby class) to University. They are copied into the database
 * the first time the server starts. Admins can add, edit and delete
 * questions afterwards from the Admin screen.
 *
 * Format: { levelId, question text, accepted answer(s) separated by ';' }
 * The difficulty is NOT written here - the DifficultyEngine works it out.
 */
namespace Bwat.Server
{
    public static class QuestionBank
    {
        public static readonly string[][] Questions =
        {
            // ---------------- 1. Nursery (Baby - Top class) ----------------
            new string[] { "1", "What is 1 + 1?", "2" },
            new string[] { "1", "What is 2 + 1?", "3" },
            new string[] { "1", "What is 3 + 2?", "5" },
            new string[] { "1", "What is 4 + 4?", "8" },
            new string[] { "1", "What is 5 - 1?", "4" },
            new string[] { "1", "What is 6 - 3?", "3" },
            new string[] { "1", "How many legs does a hen have?", "2" },
            new string[] { "1", "How many fingers are on one hand?", "5" },
            new string[] { "1", "Which number comes after 7?", "8" },
            new string[] { "1", "Which number comes before 10?", "9" },
            new string[] { "1", "What is 2 + 2 + 2?", "6" },
            new string[] { "1", "What is 10 - 5?", "5" },
            new string[] { "1", "How many legs does a cow have?", "4" },
            new string[] { "1", "What is 0 + 9?", "9" },
            new string[] { "1", "You have 3 mangoes and get 2 more. How many mangoes do you have?", "5" },
            new string[] { "1", "What is 7 + 3?", "10" },
            new string[] { "1", "What is 8 - 2?", "6" },
            new string[] { "1", "How many sides does a triangle have?", "3" },
            new string[] { "1", "What is 1 + 2 + 3?", "6" },
            new string[] { "1", "What is 9 - 9?", "0" },

            // ---------------- 2. Lower Primary (P1 - P3) ----------------
            new string[] { "2", "What is 15 + 27?", "42" },
            new string[] { "2", "What is 63 - 28?", "35" },
            new string[] { "2", "What is 6 × 7?", "42" },
            new string[] { "2", "What is 9 × 5?", "45" },
            new string[] { "2", "What is 48 ÷ 6?", "8" },
            new string[] { "2", "What is 100 - 37?", "63" },
            new string[] { "2", "Round 47 to the nearest ten.", "50" },
            new string[] { "2", "What is half of 36?", "18" },
            new string[] { "2", "How many days are in a week?", "7" },
            new string[] { "2", "What is 125 + 250?", "375" },
            new string[] { "2", "A pen costs 500 shillings. How much do 3 pens cost?", "1500" },
            new string[] { "2", "What is the value of the digit 7 in 372?", "70" },
            new string[] { "2", "What is 12 × 3?", "36" },
            new string[] { "2", "How many minutes are in one hour?", "60" },
            new string[] { "2", "What is 81 ÷ 9?", "9" },
            new string[] { "2", "What is double 45?", "90" },
            new string[] { "2", "What is 200 - 75?", "125" },
            new string[] { "2", "How many centimetres make one metre?", "100" },
            new string[] { "2", "What is 4 × 4 × 2?", "32" },
            new string[] { "2", "Write 1/2 as a decimal.", "0.5" },

            // ---------------- 3. Upper Primary (P4 - P7) ----------------
            new string[] { "3", "What is 144 ÷ 12?", "12" },
            new string[] { "3", "What is 25% of 200?", "50" },
            new string[] { "3", "What is 3/4 + 1/8? (give a fraction)", "7/8" },
            new string[] { "3", "Find the LCM of 4 and 6.", "12" },
            new string[] { "3", "Find the HCF (GCD) of 18 and 24.", "6" },
            new string[] { "3", "What is 0.6 × 0.5?", "0.3" },
            new string[] { "3", "What is the square of 13?", "169" },
            new string[] { "3", "What is the square root of 81?", "9" },
            new string[] { "3", "A rectangle is 8 cm long and 5 cm wide. What is its area in square cm?", "40" },
            new string[] { "3", "Find the perimeter of a square of side 9 cm.", "36" },
            new string[] { "3", "What is 2/3 of 27?", "18" },
            new string[] { "3", "Write 3.75 as an improper fraction in its lowest terms.", "15/4;3.75" },
            new string[] { "3", "What is 1000 - 456?", "544" },
            new string[] { "3", "Find the average of 4, 8 and 12.", "8" },
            new string[] { "3", "How many degrees are in a right angle?", "90" },
            new string[] { "3", "What is 7 × 8 + 4?", "60" },
            new string[] { "3", "Write 0.45 as a percentage (number only).", "45" },
            new string[] { "3", "A bus journey takes 2 hours 30 minutes. How many minutes is that?", "150" },
            new string[] { "3", "What is the next prime number after 13?", "17" },
            new string[] { "3", "What is (12 + 8) ÷ 4?", "5" },

            // ---------------- 4. O-Level (S1 - S4) ----------------
            new string[] { "4", "Solve for x: 2x + 5 = 17", "6" },
            new string[] { "4", "Solve for x: 3x - 7 = 14", "7" },
            new string[] { "4", "Evaluate 2^5.", "32" },
            new string[] { "4", "Evaluate 5² - 3².", "16" },
            new string[] { "4", "Find the gradient of the line through (1, 2) and (3, 8).", "3" },
            new string[] { "4", "Evaluate 3a + 2b when a = 4 and b = -1.", "10" },
            new string[] { "4", "What is 15% of 360?", "54" },
            new string[] { "4", "Find the sum of the interior angles of a pentagon (in degrees).", "540" },
            new string[] { "4", "Solve for x: x/4 = 9", "36" },
            new string[] { "4", "Find the area of a triangle with base 10 cm and height 7 cm.", "35" },
            new string[] { "4", "The ratio of boys to girls is 3:5. If there are 40 pupils, how many are girls?", "25" },
            new string[] { "4", "Evaluate √144 + √25.", "17" },
            new string[] { "4", "Expand (x + 3)(x + 4). What is the constant term?", "12" },
            new string[] { "4", "An item costs 20000 shillings and is sold at a 10% discount. What is the selling price?", "18000" },
            new string[] { "4", "Find the mean of 2, 4, 6, 8 and 10.", "6" },
            new string[] { "4", "Solve for x: 5(x - 2) = 15", "5" },
            new string[] { "4", "A right-angled triangle has shorter sides 6 and 8. Find the hypotenuse.", "10" },
            new string[] { "4", "0.00045 = 4.5 × 10^n. What is n?", "-4" },
            new string[] { "4", "What is the probability of getting a head when a fair coin is tossed?", "1/2;0.5" },
            new string[] { "4", "Find the 10th term of the sequence 3, 7, 11, 15, ...", "39" },

            // ---------------- 5. A-Level (S5 - S6) ----------------
            new string[] { "5", "If y = x³, find dy/dx at x = 2.", "12" },
            new string[] { "5", "Find the sum of the roots of x² - 5x + 6 = 0.", "5" },
            new string[] { "5", "Find the larger root of x² - 7x + 10 = 0.", "5" },
            new string[] { "5", "Evaluate log base 2 of 64.", "6" },
            new string[] { "5", "Evaluate ln(e³).", "3" },
            new string[] { "5", "If y = 4x² + 3x, find dy/dx at x = 1.", "11" },
            new string[] { "5", "Evaluate the integral of 3x² dx from x = 0 to x = 2.", "8" },
            new string[] { "5", "Find the 5th term of the geometric sequence 2, 6, 18, ...", "162" },
            new string[] { "5", "Find the sum of the first 10 positive integers.", "55" },
            new string[] { "5", "In how many ways can 4 different books be arranged on a shelf?", "24" },
            new string[] { "5", "Evaluate 5C2 (5 choose 2).", "10" },
            new string[] { "5", "Find the value of sin 30° (as a decimal).", "0.5;1/2" },
            new string[] { "5", "If f(x) = 2x + 1 and g(x) = x², find f(g(3)).", "19" },
            new string[] { "5", "Find the minimum value of y = x² - 4x + 7.", "3" },
            new string[] { "5", "Find the limit of (x² - 1)/(x - 1) as x approaches 1.", "2" },
            new string[] { "5", "Find the modulus of the complex number 3 + 4i.", "5" },
            new string[] { "5", "Solve for x: 2^x = 32", "5" },
            new string[] { "5", "Find the sum to infinity of 8 + 4 + 2 + 1 + ...", "16" },
            new string[] { "5", "If y = e^(2x), find dy/dx at x = 0.", "2" },
            new string[] { "5", "The mean of 5 numbers is 12. Four of them are 10, 11, 13 and 14. Find the fifth.", "12" },

            // ---------------- 6. University ----------------
            new string[] { "6", "Find the determinant of the matrix [[3, 2], [1, 4]].", "10" },
            new string[] { "6", "Evaluate the integral of sin x dx from 0 to π.", "2" },
            new string[] { "6", "Find the trace of the matrix [[5, 1], [2, 7]].", "12" },
            new string[] { "6", "Find the largest eigenvalue of the matrix [[2, 0], [0, 5]].", "5" },
            new string[] { "6", "If f(x, y) = x²y, find the partial derivative ∂f/∂x at (2, 3).", "12" },
            new string[] { "6", "Convert the binary number 1011 to decimal.", "11" },
            new string[] { "6", "How many edges does the complete graph K5 have?", "10" },
            new string[] { "6", "Evaluate the sum of k² for k = 1 to 5.", "55" },
            new string[] { "6", "Find the GCD of 252 and 105.", "21" },
            new string[] { "6", "Compute 17 mod 5.", "2" },
            new string[] { "6", "Find the dot product of (1, 2, 3) and (4, 5, 6).", "32" },
            new string[] { "6", "Find the population variance of 2, 4, 6 and 8.", "5" },
            new string[] { "6", "If dy/dx = 3 and y(0) = 2, find y(4).", "14" },
            new string[] { "6", "Evaluate the integral of 1/x dx from 1 to e.", "1" },
            new string[] { "6", "How many subsets does a set with 5 elements have?", "32" },
            new string[] { "6", "Find the limit of sin(x)/x as x approaches 0.", "1" },
            new string[] { "6", "Find the magnitude of the vector (2, 3, 6).", "7" },
            new string[] { "6", "Compute 3! × 2!.", "12" },
            new string[] { "6", "Find the inverse of 3 modulo 7.", "5" },
            new string[] { "6", "If y = x⁴, find the second derivative at x = 1.", "12" }
        };
    }
}
