namespace Martlet.Conversation.Tests;

// Held-out set B: written by a separate author who never read the ranking code, then run once (see the main file).
public sealed partial class PastConversationsEvaluationTests
{
    private static readonly Talk[] TalksB =
    [
        // Lasagna: the same question on different days, plus other lasagna exchanges.
        new(70, 18, 10, new Said("ls1", "How long should I bake a lasagna?",
            "About 45 minutes at 375°F with foil on top, then 15 more minutes uncovered so the cheese browns.")),
        new(41, 17, 40, new Said("ls2", "How long does lasagna need in the oven?",
            "Roughly 45 minutes covered at 375°F, then take the foil off for the last 15 minutes.")),
        new(25, 19, 5, new Said("ls5", "Can I freeze the leftover lasagna?",
            "Yes. Let it cool completely, wrap portions tightly, and it keeps for about three months.")),
        new(9, 17, 20,
            new Said("ls3", "How long do I bake lasagna again?", "45 minutes covered at 375°F, then 15 minutes uncovered."),
            new Said("ls4", "I'm out of ricotta. Will cottage cheese work?", "It works fine. Blend it smooth with an egg so the layers hold together.")),

        // Car.
        new(19, 8, 15,
            new Said("cr1", "The check engine light came on in my car this morning.",
                "Often it's a loose gas cap or an oxygen sensor. Most auto parts stores will read the code for free."),
            new Said("cr2", "The code reader says P0420.",
                "That usually points to the catalytic converter or the oxygen sensor behind it. Have a mechanic look at it before it gets worse.")),
        new(13, 17, 30, new Said("cr3", "The mechanic said it was just the oxygen sensor. $240 to replace it.",
            "That's a relief compared to a new catalytic converter. The light should stay off now.")),
        new(48, 12, 0, new Said("cr4", "What kind of oil does my Civic take?",
            "A 2018 Civic takes 0W-20 synthetic, about four and a half quarts with a filter change.")),

        // Maya's school play.
        new(15, 16, 30,
            new Said("sp1", "Maya got cast as the Cowardly Lion in her school play!", "What a fun role! When is the performance?"),
            new Said("sp2", "Two weeks from Friday. She needs a costume, any ideas?",
                "A yellow hoodie with a yarn mane sewn around the hood, brown face paint on her nose, and a braided rope for a tail."),
            new Said("sp3", "She's nervous about remembering her lines.",
                "Run them with her at dinner every night. Saying lines out loud to family builds confidence fast.")),
        new(1, 20, 0, new Said("sp4", "Maya's school play was tonight and she didn't miss a single line!",
            "Bravo to Maya! All those dinner rehearsals paid off.")),

        // Jess's wedding in Denver.
        new(22, 19, 30,
            new Said("wd1", "My friend Jess is getting married in Denver in November.", "How exciting! Are you going to the wedding?"),
            new Said("wd2", "Of course. What should I wear?",
                "Denver can be cold in November, so a dark suit or a long-sleeved cocktail dress, plus a warm coat for the photos outside."),
            new Said("wd3", "And what should I get them as a gift?",
                "Pick something from their registry. If they don't have one, a cast iron Dutch oven lasts a lifetime.")),
        new(6, 12, 45, new Said("wd4", "I booked my flight to Denver for Jess's wedding.",
            "Great! Book a hotel near the venue soon, November weekends fill up.")),

        // Learning Spanish.
        new(31, 7, 45,
            new Said("es1", "I started learning Spanish with an app on my phone.", "¡Qué bien! Ten minutes a day is enough to make steady progress."),
            new Said("es2", "How do I say 'I'm hungry' in Spanish?", "'Tengo hambre.' Literally, 'I have hunger.'")),
        new(45, 18, 50, new Said("es5", "How do you say 'where is the bathroom' in Spanish?", "'¿Dónde está el baño?'")),
        new(28, 19, 10, new Said("es6", "How do I say 'where's the bathroom' in Spanish again?", "'¿Dónde está el baño?' Baño rhymes with año.")),
        new(16, 8, 20, new Said("es7", "What's 'where is the bathroom' in Spanish?", "'¿Dónde está el baño?'")),
        new(3, 12, 30,
            new Said("es3", "I practiced Spanish with my coworker Lucía over lunch today.", "That's the best kind of practice! How did it go?"),
            new Said("es4", "Okay, but I kept mixing up ser and estar.",
                "Everyone does. Use estar for states and places that can change, and ser for who someone is and lasting traits.")),
        new(20, 18, 30, new Said("to1", "Give me a recipe for a Spanish tortilla.",
            "Slowly fry sliced potatoes and onion in olive oil, stir them into beaten eggs, cook it in a pan and flip it with a plate.")),

        // Houseplants.
        new(24, 10, 20, new Said("hp1", "My fiddle leaf fig keeps dropping leaves.",
            "That houseplant hates change. Keep it in bright indirect light, away from drafts, and let the top two inches of soil dry between waterings.")),
        new(2, 9, 10, new Said("hp2", "Now the leaves on my fig have brown edges.",
            "Brown edges usually mean dry air or uneven watering. A small humidifier and a regular watering day should help.")),
        new(44, 15, 0, new Said("hp3", "Can I root a pothos cutting in water?",
            "Yes. Put it in a jar of water with at least one node under the surface; roots show up in two or three weeks.")),

        // Bike.
        new(60, 8, 30, new Said("bk1", "What pressure should my bike tires be at?",
            "For a road bike, 80 to 100 PSI. The exact range is printed on the tire's sidewall.")),
        new(33, 7, 50, new Said("bk2", "What PSI do I pump my bike tires to again?",
            "Between 80 and 100 PSI for your road bike; check the sidewall for the exact range.")),
        new(12, 7, 40, new Said("bk3", "Remind me the right tire pressure for my road bike.", "80 to 100 PSI, a little lower when the roads are wet.")),
        new(11, 18, 15,
            new Said("bk4", "My bike chain keeps falling off when I shift.",
                "The derailleur limit screw probably needs a small turn. To get the chain back on, shift to the smallest cog and turn the pedals backward while you guide it onto the ring."),
            new Said("bk5", "Okay, I fixed it with that trick!", "Nice work! If it keeps happening, a shop tune-up will set the limit screws properly.")),
        new(4, 17, 50, new Said("bk6", "The gears on my bike skip when I pedal hard.",
            "That usually means the shift cable has stretched. Turn the barrel adjuster a quarter turn counterclockwise and test it again.")),

        // Job interview at Brightline.
        new(5, 19, 0,
            new Said("jb1", "I have a job interview at Brightline on Thursday for a project manager role.",
                "Congratulations! Do you want to practice some questions?"),
            new Said("jb2", "Yes, what will they probably ask?",
                "Expect 'tell me about a project that went wrong', 'how do you handle conflict on your team' and 'why do you want to leave your current job'. Prepare a short story for each.")),
        new(2, 18, 20,
            new Said("jb3", "I interviewed at Brightline this afternoon. I think it went well.", "That's great to hear! What did they ask you?"),
            new Said("jb4", "Mostly about a time I disagreed with my boss.",
                "That's a classic. Showing that you disagreed respectfully and still delivered is exactly what they want to hear.")),
        new(1, 10, 5, new Said("jb5", "Brightline called! They want me back for a second interview next week.",
            "Fantastic news! The second round is often with the team, so ask them how they work together.")),

        // Knitting.
        new(29, 20, 15,
            new Said("kn1", "I'm knitting a sweater for my nephew Theo.", "How sweet! What yarn are you using?"),
            new Said("kn2", "A soft merino, but I don't know how many skeins to buy.",
                "For a child's size 6 in worsted weight, plan on about five skeins, plus a spare in case the dye lots differ.")),
        new(7, 20, 40, new Said("kn4", "I want to knit a matching sweater for my niece too.",
            "A matching pair would be adorable. Same pattern in a different color?")),

        // Camping.
        new(10, 19, 45,
            new Said("cp1", "We're going camping at Pinecrest Lake later this month.", "Pinecrest is beautiful. Do you have a campsite reserved?"),
            new Said("cp2", "Yes, site 42. How cold does it get up there at night?",
                "In October it can drop to around 35°F, so bring sleeping bags rated to 20 degrees and warm hats."),
            new Said("cp3", "Can we have a campfire?", "Check the fire restrictions the week you go; in dry years they often allow only gas stoves.")),

        // Knee.
        new(23, 11, 20, new Said("dr1", "My knee hurts when I walk down stairs.",
            "That can be the kneecap not tracking well. If it lasts more than a couple of weeks, see a doctor.")),
        new(14, 15, 40, new Said("dr2", "The doctor says it's tendinitis and I should do physical therapy twice a week.",
            "Physical therapy works well for that. Stronger hips and thighs take pressure off the knee.")),
        new(3, 8, 0, new Said("dr3", "My physical therapist gave me wall sits and step-downs to do at home.",
            "Good exercises. Go slowly on the step-downs and stop if the pain turns sharp.")),

        // Board game night (household).
        new(9, 19, 30,
            new Said("gn1", "We're hosting a board game night on Saturday for six people.",
                "Fun! For six, Codenames or Wavelength work well with a big group.", "Priya"),
            new Said("gn2", "I want to play Catan at game night.", "Catan works best with three or four. For six you'd need the expansion.", "Dev"),
            new Said("gn3", "Let's start with Codenames and play Catan later with whoever stays.",
                "Good plan. Pick snacks that won't get the cards greasy.", "Priya")),
        new(7, 22, 15, new Said("gn4", "Game night was a blast. Dev won Catan twice.",
            "Sounds like Dev has a strategy. Did Codenames go over well?", "Priya")),

        // Podcast.
        new(52, 7, 20, new Said("pc1", "Can you suggest a history podcast for my commute?",
            "Try 'The Rest Is History': two historians telling stories, about 45 minutes an episode.")),
        new(21, 7, 30, new Said("pc2", "I just finished their episode about the fall of Constantinople.",
            "That's one of their best. If you liked it, the Crusades series is next.")),

        // Faucets.
        new(10, 8, 40,
            new Said("fc1", "Our kitchen faucet keeps dripping.",
                "Usually the cartridge inside is worn out. Turn off the water under the sink before you take the handle off."),
            new Said("fc2", "What tools will I need?", "An adjustable wrench, a hex key for the handle screw, and a new cartridge of the same brand.")),
        new(6, 18, 30, new Said("fc4", "I replaced the cartridge and the drip finally stopped!", "Great job! You just saved yourself a plumber's visit.")),
        new(50, 19, 20, new Said("fc3", "The bathroom faucet is leaking around the base.",
            "The O-ring under the spout is probably worn. Shut off the water, pull the spout and swap in a new O-ring.")),

        // Living room paint (household).
        new(20, 11, 0,
            new Said("pt1", "I think we should paint the living room sage green.", "Sage green is calming and goes well with wood floors.", "Priya"),
            new Said("pt2", "I'd rather do the living room in a warm gray.",
                "Warm gray is a cozy, safe choice. You could paint a big test swatch of each.", "Dev")),
        new(4, 10, 30, new Said("pt3", "We picked up paint samples for the living room today.",
            "Paint big squares on two walls and look at them in morning and evening light.", "Priya")),

        // A mystery novel.
        new(20, 21, 15, new Said("nv3", "I started a mystery novel called The Lantern House.",
            "A good mystery is perfect for autumn evenings. No spoilers, I promise.")),
        new(2, 22, 40,
            new Said("nv1", "I just finished The Lantern House. I can't believe the gardener was the killer!",
                "What a twist! Did you suspect him at all before the last chapter?"),
            new Said("nv2", "Not even a little. The muddy boots were the clue all along.", "Go back to the first chapter, the boots show up on page three.")),

        // Mixed small talk.
        new(5, 16, 0,
            new Said("lv1", "The leaves in the yard are piling up.", "Rake them onto a tarp and use them as mulch, or bag them for the yard waste pickup."),
            new Said("wl1", "Can my wool sweaters go in the dryer?", "No, they'll shrink. Wash them cold on the wool cycle and dry them flat on a towel.")),
        new(8, 9, 0,
            new Said("cf1", "A new coffee chain opened downtown.", "Is it better than your usual café?"),
            new Said("cf2", "No, the lattes tasted burnt.", "Then your usual café keeps its crown."),
            new Said("ra1", "I heard a great radio interview with a chef this morning.", "Which chef? I'd love to hear what they talked about.")),
        new(18, 21, 45,
            new Said("bb1", "Did you catch the baseball game last night?", "I couldn't watch it, but I heard it went to extra innings."),
            new Said("dw1", "Is the new Doctor Who season any good?", "Reviews are mixed, but most people loved the Christmas special."),
            new Said("wg1", "Let's play twenty questions.", "Okay, I'm thinking of something. Ask your first question!")),
        new(37, 9, 20,
            new Said("bo1", "My work boots are wearing out at the heels.", "A cobbler can replace the heels for much less than a new pair costs."),
            new Said("sh1", "What tools do I need to build a simple bookshelf?", "A drill, a saw, a level, wood screws and some sandpaper."),
            new Said("ci1", "My car insurance went up three hundred dollars at renewal.",
                "Get quotes from two other insurers; bundling it with renters insurance often brings the price down.")),
        new(13, 16, 45,
            new Said("sc1", "Maya has soccer practice every Tuesday and Thursday now.", "I'll remind you on those days. Does she need new cleats?"),
            new Said("hw1", "Any costume ideas for my office Halloween party?",
                "Go as a houseplant: green clothes and a paper flowerpot around your waist. Low effort, big laughs.")),
    ];

    private static readonly Question[] QuestionsB =
    [
        new("B1", "Have I ever asked you about lasagna?", null, null, ["ls1", "ls2", "ls3"], ["ls5"], ["ls4"]),
        new("B2", "What did you tell me about lasagna last week?", null, null, ["ls3"], ["ls4"]),
        new("B3", "Do you recall what fixes a bike chain that falls off?", null, null, ["bk4"], ["bk5"]),
        new("B4", "What did you tell me on Tuesday about my bike?", null, null, ["bk6"]),
        new("B5", "Did I mention anything about my bicycle?", null, null, ["bk6"], ["bk4"], ["bk1", "bk2", "bk3"]),
        new("B6", "Remember when I was interviewing at Brightline?", null, null, ["jb3"], ["jb4"], ["jb1"]),
        new("B7", "Did I tell you about my job interviews?", null, null, ["jb3"], ["jb1"], ["jb5"]),
        new("B8", "What did I tell you yesterday about Brightline?", null, null, ["jb5"]),
        new("B9", "Do you remember how much yarn I needed for the sweaters?", null, null, ["kn2"], ["kn1"]),
        new("B10", "Do you remember what kind of sleeping bag you said to bring camping?", null, null, ["cp2"], ["cp1"]),
        new("B11", "You said I needed some tools to fix the faucet last week. Which ones?", null, null, ["fc2"], ["fc1"]),
        new("B12", "What did I say a couple of days ago about my fiddle leaf fig?", null, null, ["hp2"]),
        new("B13", "Did we ever talk about my houseplants?", null, null, ["hp1"], ["hp2"], ["hp3"]),
        new("B14", "Yesterday I told you about a book I'd just finished. What was it called?", null, null, ["nv1"], ["nv2"]),
        new("B15", "Can you recall which game Dev wanted to play at game night?", null, null, ["gn2"]),
        new("B16", "Do you recall Priya's idea for the living room walls?", null, null, ["pt1"]),
        new("B17", "Three days ago I told you how practicing Spanish went, remember?", null, null, ["es3"], ["es4"]),
        new("B18", "Do you remember the costume idea for Maya's play?", null, null, ["sp2"], ["sp1"]),
        new("BT1", null, "leaky faucet", null, ["fc1"], ["fc3"], ["fc4"], ["fc2"]),
        new("BT2", null, "Dev paint color", null, ["pt2"]),
        new("BT3", null, "vehicle repair", null, ["cr3"], ["cr1"], ["cr2"]),
        new("BT4", null, "bike tire pressure", null, ["bk1", "bk2", "bk3"]),
        new("BT5", null, "Spanish phrases", null, ["es5", "es6", "es7"], ["es2"]),
        new("BT6", null, "doctor knee", "last week", ["dr2"], ["dr3"]),
        new("BT7", null, "physiotherapy exercises", null, ["dr3"], ["dr2"]),
        new("BT8", null, "podcast recommendations", null, ["pc1"], ["pc2"]),
        new("BT9", null, "game night", "Saturday", ["gn4"]),
        new("BT10", null, "what to wear to the wedding", null, ["wd2"], ["wd1"]),
    ];
}
