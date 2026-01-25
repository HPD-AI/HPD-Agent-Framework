// Zoo Activities and Games

// Animal Trivia Game
const triviaQuestions = [
    {
        question: "What is the largest land animal?",
        options: ["Elephant", "Giraffe", "Lion", "Rhino"],
        correct: 0
    },
    {
        question: "How fast can a cheetah run?",
        options: ["50 mph", "70 mph", "90 mph", "110 mph"],
        correct: 1
    },
    {
        question: "Which animal is known as the 'King of the Jungle'?",
        options: ["Tiger", "Lion", "Bear", "Elephant"],
        correct: 1
    },
    {
        question: "What do pandas mainly eat?",
        options: ["Fish", "Meat", "Bamboo", "Fruits"],
        correct: 2
    },
    {
        question: "How many hearts does an octopus have?",
        options: ["1", "2", "3", "4"],
        correct: 2
    },
    {
        question: "Which bird can fly backwards?",
        options: ["Eagle", "Hummingbird", "Owl", "Penguin"],
        correct: 1
    },
    {
        question: "What is a group of lions called?",
        options: ["Pack", "Herd", "Pride", "Flock"],
        correct: 2
    },
    {
        question: "How long can a tortoise live?",
        options: ["50 years", "75 years", "100+ years", "150 years"],
        correct: 2
    },
    {
        question: "Which animal has the longest neck?",
        options: ["Elephant", "Giraffe", "Ostrich", "Camel"],
        correct: 1
    },
    {
        question: "What type of animal is a shark?",
        options: ["Mammal", "Reptile", "Fish", "Amphibian"],
        correct: 2
    }
];

let currentQuestion = 0;
let triviaScore = 0;
let triviaStarted = false;

function startTrivia() {
    currentQuestion = 0;
    triviaScore = 0;
    triviaStarted = true;
    document.getElementById('triviaScore').textContent = '0';
    showQuestion();
}

function showQuestion() {
    const questionEl = document.getElementById('triviaQuestion');
    const optionsEl = document.getElementById('triviaOptions');

    if (currentQuestion >= triviaQuestions.length) {
        questionEl.textContent = `Game Over! Your score: ${triviaScore}/${triviaQuestions.length}`;
        optionsEl.innerHTML = '';
        return;
    }

    const q = triviaQuestions[currentQuestion];
    questionEl.textContent = q.question;
    optionsEl.innerHTML = '';

    q.options.forEach((option, index) => {
        const optionEl = document.createElement('div');
        optionEl.className = 'trivia-option';
        optionEl.textContent = option;
        optionEl.onclick = () => selectAnswer(index);
        optionsEl.appendChild(optionEl);
    });
}

function selectAnswer(index) {
    const q = triviaQuestions[currentQuestion];
    const options = document.querySelectorAll('.trivia-option');

    options.forEach((opt, i) => {
        opt.onclick = null;
        if (i === q.correct) {
            opt.classList.add('correct');
        } else if (i === index && index !== q.correct) {
            opt.classList.add('wrong');
        }
    });

    if (index === q.correct) {
        triviaScore++;
        document.getElementById('triviaScore').textContent = triviaScore;
        showFloatingMessage('Correct! 🎉');
    } else {
        showFloatingMessage('Wrong! 😅');
    }

    currentQuestion++;
    setTimeout(showQuestion, 1500);
}

// Animal Wheel Game
const animals = [
    { emoji: '🦁', name: 'Lion', fact: 'Lions are the only cats that live in groups called prides!' },
    { emoji: '🐘', name: 'Elephant', fact: 'Elephants can recognize themselves in mirrors!' },
    { emoji: '🦒', name: 'Giraffe', fact: 'A giraffe\'s tongue is about 20 inches long!' },
    { emoji: '🐼', name: 'Panda', fact: 'Baby pandas are born pink and about the size of a stick of butter!' },
    { emoji: '🦓', name: 'Zebra', fact: 'No two zebras have the same stripe pattern!' },
    { emoji: '🐻', name: 'Bear', fact: 'Bears can run up to 35 mph!' },
    { emoji: '🦊', name: 'Fox', fact: 'Foxes use Earth\'s magnetic field to hunt!' },
    { emoji: '🐺', name: 'Wolf', fact: 'Wolves can hear sounds up to 10 miles away!' },
    { emoji: '🦅', name: 'Eagle', fact: 'Eagles can see fish from over a mile away!' },
    { emoji: '🦜', name: 'Parrot', fact: 'Some parrots can live for over 80 years!' }
];

function spinAnimalWheel() {
    const display = document.getElementById('animalDisplay');
    const factDisplay = document.getElementById('animalFact');

    display.classList.add('spinning');
    factDisplay.textContent = 'Spinning...';

    let spins = 0;
    const maxSpins = 20;
    const spinInterval = setInterval(() => {
        const randomAnimal = animals[Math.floor(Math.random() * animals.length)];
        display.textContent = randomAnimal.emoji;
        spins++;

        if (spins >= maxSpins) {
            clearInterval(spinInterval);
            display.classList.remove('spinning');

            const finalAnimal = animals[Math.floor(Math.random() * animals.length)];
            display.textContent = finalAnimal.emoji;
            factDisplay.textContent = finalAnimal.fact;

            createConfetti();
        }
    }, 100);
}

// Visit Price Calculator
const prices = {
    adult: 25,
    child: 15,
    senior: 20,
    family: 80
};

function calculatePrice() {
    const dateInput = document.getElementById('visitDate');
    const typeSelect = document.getElementById('visitType');
    const priceDisplay = document.getElementById('priceDisplay');

    if (!dateInput.value) {
        priceDisplay.textContent = 'Please select a date!';
        return;
    }

    const selectedDate = new Date(dateInput.value);
    const dayOfWeek = selectedDate.getDay();
    const ticketType = typeSelect.value;

    let basePrice = prices[ticketType];
    let totalPrice = basePrice;

    // Weekend surcharge (Saturday = 6, Sunday = 0)
    if (dayOfWeek === 6 || dayOfWeek === 0) {
        totalPrice = Math.round(totalPrice * 1.1); // 10% weekend surcharge
    }

    const ticketNames = {
        adult: 'Adult',
        child: 'Child',
        senior: 'Senior',
        family: 'Family Pack'
    };

    priceDisplay.innerHTML = `
        <div>${ticketNames[ticketType]} Ticket</div>
        <div>Base: $${basePrice}</div>
        ${dayOfWeek === 6 || dayOfWeek === 0 ? '<div>Weekend Surcharge: +10%</div>' : ''}
        <div style="font-size: 1.4rem; color: #2e7d32;">Total: $${totalPrice}</div>
    `;
}

// Stamp Collection
const zooStamps = ['🦁', '🐘', '🦒', '🦜', '🐊', '🐠', '🐼', '🦋'];
let collectedStamps = [];

function initStampGrid() {
    const stampGrid = document.getElementById('stampGrid');
    stampGrid.innerHTML = '';

    zooStamps.forEach((stamp, index) => {
        const stampEl = document.createElement('div');
        stampEl.className = 'stamp';
        stampEl.id = `stamp-${index}`;
        stampGrid.appendChild(stampEl);
    });
}

function collectStamp() {
    const uncollected = zooStamps.filter((_, index) =>
        !collectedStamps.includes(index)
    );

    if (uncollected.length === 0) {
        showFloatingMessage('All stamps collected! 🏆');
        return;
    }

    const randomIndex = Math.floor(Math.random() * uncollected.length);
    const stampIndex = zooStamps.indexOf(uncollected[randomIndex]);

    if (stampIndex !== -1 && !collectedStamps.includes(stampIndex)) {
        collectedStamps.push(stampIndex);
        const stampEl = document.getElementById(`stamp-${stampIndex}`);
        stampEl.textContent = zooStamps[stampIndex];
        stampEl.classList.add('collected');

        document.getElementById('stampCount').textContent = collectedStamps.length;

        if (collectedStamps.length === zooStamps.length) {
            showFloatingMessage('Congratulations! You collected all stamps! 🎉');
            createConfetti();
        } else {
            showFloatingMessage(`Collected: ${zooStamps[stampIndex]}!`);
        }
    }
}

// Helper Functions
function showFloatingMessage(message) {
    const existingMessage = document.querySelector('.floating-message');
    if (existingMessage) {
        existingMessage.remove();
    }

    const floatingMsg = document.createElement('div');
    floatingMsg.className = 'floating-message';
    floatingMsg.textContent = message;

    document.body.appendChild(floatingMsg);

    setTimeout(() => {
        floatingMsg.style.animation = 'fadeOut 0.5s ease-out forwards';
        setTimeout(() => floatingMsg.remove(), 500);
    }, 1500);
}

function createConfetti() {
    const colors = ['#ff0000', '#00ff00', '#0000ff', '#ffff00', '#ff00ff', '#00ffff'];

    for (let i = 0; i < 50; i++) {
        const confetti = document.createElement('div');
        confetti.style.cssText = `
            position: fixed;
            top: -20px;
            left: ${Math.random() * 100}vw;
            width: 10px;
            height: 10px;
            background: ${colors[Math.floor(Math.random() * colors.length)]};
            z-index: 9999;
            animation: fall ${2 + Math.random() * 3}s linear forwards;
            animation-delay: ${Math.random() * 0.5}s;
        `;

        document.body.appendChild(confetti);
        setTimeout(() => confetti.remove(), 5000);
    }

    // Add fall animation if not exists
    if (!document.querySelector('#confetti-style')) {
        const style = document.createElement('style');
        style.id = 'confetti-style';
        style.textContent = `
            @keyframes fall {
                to {
                    transform: translateY(100vh) rotate(720deg);
                    opacity: 0;
                }
            }
        `;
        document.head.appendChild(style);
    }
}

// Add fadeOut animation
const fadeOutStyle = document.createElement('style');
fadeOutStyle.textContent = `
    @keyframes fadeOut {
        from {
            opacity: 1;
            transform: translate(-50%, -50%) scale(1);
        }
        to {
            opacity: 0;
            transform: translate(-50%, -50%) scale(1.2);
        }
    }
`;
document.head.appendChild(fadeOutStyle);

// Initialize
document.addEventListener('DOMContentLoaded', () => {
    initStampGrid();

    // Add smooth scrolling for navigation
    document.querySelectorAll('a[href^="#"]').forEach(anchor => {
        anchor.addEventListener('click', function(e) {
            e.preventDefault();
            const target = document.querySelector(this.getAttribute('href'));
            if (target) {
                target.scrollIntoView({
                    behavior: 'smooth',
                    block: 'start'
                });
            }
        });
    });
});

// Console Easter Egg
console.log('%c🦁 Welcome to Amazing Zoo! 🦁', 'font-size: 24px; color: #2e7d32; font-weight: bold;');
console.log('%cTry the trivia game, spin the wheel, or collect stamps!', 'font-size: 14px; color: #1b5e20;');
console.log('%c🎮 Pro tip: Explore all the activities!', 'font-size: 12px; color: #4caf50;');
