#!/usr/bin/env python3
"""Seed corpus for an elderly Hebrew-speaking academic (deterministic, ~60 files).

  podman run --rm -v tools/seed-docs:/src:Z -v ~/vm/shamash-win11/shared/seed:/out:Z docker.io/library/python:3.12 \
    sh -c 'apt-get update -qq && apt-get install -y -qq fonts-dejavu-core >/dev/null && \
           pip install -q python-docx openpyxl reportlab python-bidi && python /src/gen.py /out'
"""
import os, sys, random, io, shutil, datetime as dt
from docx import Document
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
import openpyxl
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.pagesizes import A4
from bidi.algorithm import get_display

OUT = sys.argv[1] if len(sys.argv) > 1 else "seed"
ANCHOR = dt.datetime(2026, 11, 20, 10, 0)
rng = random.Random(1948)
pdfmetrics.registerFont(TTFont("DejaVu", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"))

HE = {
"jer": ["ירושלים בתקופה העות'מאנית עברה שינויים דמוגרפיים ומנהליים עמוקים מאז הכיבוש ב-1517.",
 "הסולטאן סולימאן המפואר בנה מחדש את חומות העיר, והן עומדות עד היום סביב העיר העתיקה.",
 "בתקופה העות'מאנית המאוחרת גדלה האוכלוסייה היהודית, ושכונות חדשות קמו מחוץ לחומות.",
 "פקידי הממשל העות'מאני ניהלו את העיר באמצעות המג'לס, מועצה מקומית של נכבדים מכל העדות.",
 "הקונסוליות הזרות שנפתחו באמצע המאה התשע עשרה שינו את מאזן הכוחות בעיר.",
 "רשומות בתי הדין השרעיים מספקות חומר עשיר על חיי היומיום, על בעלות ועל מסחר בעיר.",
 "מסעות עולי הרגל והתיירות הנוצרית הניעו את כלכלת העיר ואת בניית בתי ההארחה.",
 "מחקר חדש בארכיונים באיסטנבול מאפשר לבחון מחדש את מדיניות המסים כלפי תושבי ירושלים."],
"press": ["העיתונות העברית בארץ ישראל החלה בשלהי המאה התשע עשרה בעיתונים שבועיים קטנים.",
 "אליעזר בן יהודה ראה בעיתון כלי להחייאת השפה ולחידוש מילים חדשות.",
 "הצנזורה העות'מאנית הגבילה את תוכן העיתונים ולעיתים סגרה אותם לחודשים ארוכים.",
 "המפרסמים נאבקו במצוקת נייר, בציוד דפוס ישן ובמספר מנויים מצומצם.",
 "בעיתונות התקיים ויכוח נוקב על אופיו של היישוב החדש ועל יחסו לתושבים הערבים.",
 "מדורי הספרות שבעיתונים שימשו במה לסופרים צעירים ולשירה מקורית."],
"print": ["הדפוס העברי הראשון בארץ ישראל הוקם בצפת במאה השש עשרה.",
 "בתי דפוס בוונציה ובאמסטרדם הפיצו ספרים עבריים לכל קהילות ישראל.",
 "בחירת האותיות ועיצוב הדף השפיעו על קריאת הטקסטים לאורך דורות.",
 "קטלוג הספרים העבריים המודפסים הוא כלי יסוד לכל חוקר של תולדות הספר.",
 "מחקר השוואתי של מהדורות ראשונות חושף תיקונים שעשו המדפיסים בעת ההדפסה.",
 "ספריות פרטיות של רבנים וסוחרים מעידות על תפוצת הספר ועל טעמם של הקוראים."],
"edu": ["הוראת ההיסטוריה בבתי הספר מתמודדת עם הצורך לשלב מקורות ראשוניים בשיעור.",
 "תלמידים נוטים לראות בהיסטוריה רשימת תאריכים ולא סיפור של אנשים ובחירות.",
 "ניתוח מסמכים מקוריים מפתח חשיבה ביקורתית ויכולת לשאול שאלות טובות.",
 "ההכשרה של מורים להיסטוריה צריכה לכלול היכרות עם עבודת ארכיון.",
 "שימוש בטכנולוגיה בכיתה מאפשר גישה לסריקות של כתבי יד ולמפות היסטוריות.",
 "מבחני הבגרות בהיסטוריה משקפים תפיסה מסוימת של מה שחשוב לדעת."],
"sef": ["הקהילות הספרדיות בבלקן קיימו מסורת עשירה של לימוד תורה ושל ספרות לדינו.",
 "גירוש ספרד ב-1492 פיזר את היהודים לאימפריה העות'מאנית ולצפון אפריקה.",
 "קהילת סלוניקי הייתה במשך דורות מרכז תרבותי ומסחרי יהודי חשוב.",
 "פרנסתם של יהודים רבים הייתה על מסחר בין הנמלים של הים התיכון.",
 "שירי הרומנסה נשמרו בעל פה, ואוספים מהם נרשמו רק במאה העשרים.",
 "הקשרים המשפחתיים בין קהילות נשמרו באמצעות חליפת מכתבים ושידוכים."],
"lit": ["הספרות העברית החדשה נולדה בין תנועת ההשכלה לבין התחייה הלאומית.",
 "ביאליק וטשרניחובסקי עיצבו שפה פיוטית חדשה המבוססת על מקורות עתיקים.",
 "הרומן העברי הראשון נכתב בהשפעת הספרות הרוסית והגרמנית של התקופה.",
 "דור המדינה העמיד במרכז את הצבר, את הנוף המקומי ואת הדיבור הישיר.",
 "מחקר הספרות שואל כיום גם על קולות שנדחקו לשוליים של הקאנון.",
 "תרגום ספרות מופת לעברית העשיר את הלשון ואת אוצר המילים."],
"arch": ["גניזת קהיר היא אחד מאוספי כתבי היד החשובים ביותר לחקר ימי הביניים.",
 "מסמכי הגניזה מגלים מסחר, נישואין ומחלוקות של יהודים בעולם הערבי.",
 "שימור כתבי יד דורש בקרת לחות וטמפרטורה ושימוש בחומרים נטולי חומצה.",
 "סריקה דיגיטלית מרחיבה את הגישה לחוקרים, אך אינה מחליפה את הבדיקה של הנייר.",
 "קטלוג מסודר הוא תנאי בסיסי לכל מחקר שמסתמך על אוספי ארכיון.",
 "ארכיונים משפחתיים שנתרמו לספרייה הלאומית מאירים את חיי היומיום של דורות עברו."],
"trav": ["מסעות עולי הרגל לארץ הקודש תועדו ביומנים של נוסעים מאירופה ומצפון אפריקה.",
 "הנוסעים תיארו את מצב הדרכים, את המסים ואת הסכנות שליוו את המסע.",
 "תיאורי ירושלים ביומנים אלה שונים זה מזה, לפי זהות הכותב ומטרת הביקור.",
 "הנוסעים הנוצרים התעניינו במקומות הקדושים, והיהודים בקברי צדיקים ובבתי כנסת.",
 "מפות ושרטוטים שצורפו ליומנים הם מקור חשוב לגאוגרפיה היסטורית.",
 "השוואה בין יומנים מתקופות שונות חושפת את השתנות העיר ואת שינוי הנופים."]}
EN = {
"jer": ["Jerusalem under Ottoman rule, from 1517 to 1917, was a provincial city of modest size but great symbolic weight.",
 "Suleiman the Magnificent rebuilt the city walls, which still define the Old City today.",
 "The late Ottoman period saw the growth of the Jewish population and of new neighbourhoods outside the walls.",
 "The local council, the majlis, brought together notables of all communities to manage municipal affairs.",
 "Foreign consulates opened in the mid nineteenth century and shifted the balance of influence in the city.",
 "Shari'a court registers offer rich evidence on property, trade and daily life in Ottoman Jerusalem.",
 "Pilgrimage and Christian tourism drove the local economy and the building of hospices.",
 "New work in the Istanbul archives allows a fresh look at taxation policy toward the city's inhabitants."],
"hist": ["Historians of the modern Middle East have increasingly relied on provincial archives rather than imperial decrees.",
 "The Tanzimat reforms changed the legal status of non-Muslim communities across the empire.",
 "Trade routes through Mediterranean ports shaped the fortunes of merchant families for generations.",
 "Oral testimony complements the written record, but must be handled with methodological care.",
 "Comparative study of three port cities shows how local elites adapted to imperial policy.",
 "Digitised newspapers have opened a new field of research on public opinion before 1914."],
"meth": ["A research proposal should state its question, its sources and its limits in the first page.",
 "Footnotes are a conversation with earlier scholars; they should be precise and generous.",
 "Peer review improves an article most when the reviewer is also a reader who wants to learn something.",
 "A conference paper should be shorter than the written article and organised around one argument.",
 "Archival work requires patience: finding aids are often incomplete and boxes are mislabelled.",
 "Teaching and research feed each other; a good seminar question often becomes a chapter."]}
HEAD_HE = ["מבוא", "רקע היסטורי", "דיון", "מקורות", "סיכום"]
HEAD_EN = ["Introduction", "Background", "Discussion", "Sources", "Conclusion"]

def paras(lang, topic, n=4, skip=None):
    pool = (HE if lang == "he" else EN)[topic]
    out = []
    for i in range(n):
        out.append(" ".join(rng.sample(pool, min(3, len(pool)))))
    return out

def rtl_par(doc, text, style=None):
    p = doc.add_paragraph(style=style)
    pPr = p._p.get_or_add_pPr()
    pPr.append(OxmlElement("w:bidi"))
    r = p.add_run(text)
    rPr = r._r.get_or_add_rPr()
    rPr.append(OxmlElement("w:rtl"))
    return p

def build_docx(lang, title, body, when):
    d = Document()
    d.core_properties.created = d.core_properties.modified = when
    d.core_properties.author = "פרופ' כהן" if lang == "he" else "Prof. Cohen"
    d.core_properties.title = title
    heads = HEAD_HE if lang == "he" else HEAD_EN
    if lang == "he":
        rtl_par(d, title, "Title")
        for i, t in enumerate(body):
            rtl_par(d, heads[i % len(heads)], "Heading 1")
            rtl_par(d, t)
    else:
        d.add_paragraph(title, "Title")
        for i, t in enumerate(body):
            d.add_paragraph(heads[i % len(heads)], "Heading 1")
            d.add_paragraph(t)
    b = io.BytesIO(); d.save(b); return b.getvalue()

def build_pdf(lang, title, body, when):
    b = io.BytesIO()
    c = canvas.Canvas(b, pagesize=A4, invariant=1)
    W, H = A4
    he = lang == "he"
    def line_out(y, text, size):
        c.setFont("DejaVu", size)
        if he: c.drawRightString(W - 60, y, get_display(text))
        else: c.drawString(60, y, text)
    y = H - 80
    line_out(y, title, 16); y -= 34
    for para in body:
        words, cur, lines = para.split(), "", []
        for w in words:
            t = (cur + " " + w).strip()
            if pdfmetrics.stringWidth(t, "DejaVu", 11) > W - 120:
                lines.append(cur); cur = w
            else: cur = t
        lines.append(cur)
        for l in lines:
            line_out(y, l, 11); y -= 16
        y -= 10
    c.setTitle(title); c.save(); return b.getvalue()

def build_xlsx(kind, when):
    wb = openpyxl.Workbook(); ws = wb.active
    wb.properties.created = wb.properties.modified = when
    if kind == "grades":
        ws.title = "ציונים"; ws.sheet_view.rightToLeft = True
        ws.append(["שם הסטודנט", "עבודה", "בחינה", "ציון סופי"])
        names = ["דנה לוי", "יוסי מזרחי", "רות אברהם", "אורי פרידמן", "נועה כץ", "אלון שלום", "מיכל ברק", "תמר גולן", "עידו רוזן", "שירה דוד"]
        for i, n in enumerate(names, 2):
            a, e = rng.randint(70, 100), rng.randint(60, 100)
            ws.append([n, a, e, f"=ROUND(B{i}*0.4+C{i}*0.6,0)"])
    elif kind == "budget":
        ws.title = "תקציב"; ws.sheet_view.rightToLeft = True
        ws.append(["סעיף", "סכום (ש\"ח)"])
        for n, v in [("דמי הרשמה לכנס", 1200), ("טיסה", 2800), ("מלון", 3400), ("ספרים", 900), ("צילום מסמכים בארכיון", 450)]:
            ws.append([n, v])
        ws.append(["סה\"כ", "=SUM(B2:B6)"])
    else:
        ws.title = "Budget"
        ws.append(["Item", "Amount (USD)"])
        for n, v in [("Registration", 350), ("Flights", 800), ("Hotel", 950), ("Archive fees", 120)]:
            ws.append([n, v])
        ws.append(["Total", "=SUM(B2:B5)"])
    b = io.BytesIO(); wb.save(b); return b.getvalue()

files = []  # (relpath, bytes, datetime)
def add(rel, data, when): files.append((rel, data, when))
def date(days_ago): return ANCHOR - dt.timedelta(days=days_ago, hours=rng.randint(0, 9), minutes=rng.randint(0, 59))

def doc(rel, lang, title, topic, n=4, days=None, fmt="docx", body=None):
    when = date(days if days is not None else rng.randint(5, 1090))
    body = body or paras(lang, topic, n)
    data = (build_docx if fmt == "docx" else build_pdf)(lang, title, body, when)
    add(rel, data, when)
    return data, body, when

# ---- Jerusalem (content search targets)
doc("מאמרים/ירושלים בתקופה העות'מאנית - סקירה.docx", "he", "ירושלים בתקופה העות'מאנית: סקירה", "jer", 5, 210)
doc("הרצאות/העיר העתיקה תחת שלטון הסולטאנים.docx", "he", "העיר העתיקה תחת שלטון הסולטאנים", "jer", 4, 480)
doc("Desktop stuff/טיוטה - ירושלים העות'מאנית.docx", "he", "טיוטה: ירושלים העות'מאנית והיישוב היהודי", "jer", 3, 12)
jen, jbody, jwhen = doc("Articles (English)/Jerusalem under Ottoman rule.docx", "en", "Jerusalem under Ottoman rule", "jer", 5, 330)
doc("Articles (English)/Jerusalem Ottoman period notes.pdf", "en", "Notes on Jerusalem in the Ottoman period", "jer", 3, 90, "pdf")

# ---- Hebrew articles (some pdf)
art = [("העיתונות העברית בארץ ישראל", "press", 700), ("תולדות הדפוס העברי", "print", 820),
       ("קהילות ספרדיות בבלקן", "sef", 950), ("על הספרות העברית החדשה", "lit", 560),
       ("גניזה וארכיון", "arch", 400), ("יומני נוסעים בארץ הקודש", "trav", 280),
       ("הוראת ההיסטוריה בבית הספר", "edu", 150)]
saved = {}
for t, k, d in art:
    saved[t] = doc(f"מאמרים/{t}.docx", "he", t, k, 4, d)
for t, k, d in [("סיכום מחקר על דפוס בוונציה", "print", 620), ("הערות על ארכיוני איסטנבול", "arch", 340), ("ספרות ומחקר", "lit", 770)]:
    doc(f"מאמרים/{t}.pdf", "he", t, k, 3, d, "pdf")

# ---- old articles (older drafts; near duplicates)
for t in ["העיתונות העברית בארץ ישראל", "קהילות ספרדיות בבלקן", "על הספרות העברית החדשה"]:
    _, body, when = saved[t]
    older = body[:1] + body[2:]  # one paragraph missing
    add(f"מאמרים/ישנים/{t} - גרסה ראשונה.docx", build_docx("he", t, older, when - dt.timedelta(days=200)),
        when - dt.timedelta(days=200))
_, body, when = saved["תולדות הדפוס העברי"]
add("מאמרים/ישנים/תולדות הדפוס העברי (ישן).docx", build_docx("he", "תולדות הדפוס העברי", body[:-1], when - dt.timedelta(days=300)), when - dt.timedelta(days=300))
# near dup of the English Jerusalem article
add("Articles (English)/Jerusalem under Ottoman rule - draft.docx", build_docx("en", "Jerusalem under Ottoman rule", jbody[:3] + jbody[4:], jwhen), jwhen - dt.timedelta(days=150))

# ---- exact duplicates (same bytes)
def dup(src, dst, extra_days=0):
    d, w = next((d, w) for r, d, w in files if r == src); add(dst, d, w - dt.timedelta(days=extra_days))
dup("מאמרים/תולדות הדפוס העברי.docx", "מאמרים/עותק של תולדות הדפוס העברי.docx")
dup("מאמרים/גניזה וארכיון.docx", "מאמרים/גניזה וארכיון (1).docx")
dup("מאמרים/יומני נוסעים בארץ הקודש.docx", "הרצאות/יומני נוסעים בארץ הקודש.docx")
dup("מאמרים/ירושלים בתקופה העות'מאנית - סקירה.docx", "Desktop stuff/ירושלים בתקופה העות'מאנית - סקירה (1).docx", 0)
dup("Articles (English)/Jerusalem under Ottoman rule.docx", "Articles (English)/Copy of Jerusalem under Ottoman rule.docx")
dup("מאמרים/הוראת ההיסטוריה בבית הספר.docx", "Desktop stuff/הוראת ההיסטוריה בבית הספר - עותק.docx")

# ---- English articles
for t, k, d in [("Ottoman provincial archives: a survey", "hist", 600), ("Writing a research proposal", "meth", 240),
                ("Peer review and its discontents", "meth", 900), ("Port cities and merchant families", "hist", 430)]:
    doc(f"Articles (English)/{t}.docx", "en", t, k, 4, d)
doc("Articles (English)/Tanzimat reforms - summary.pdf", "en", "Tanzimat reforms: a summary", "hist", 3, 520, "pdf")

# ---- lectures
for t, k, d in [("הרצאה - העיתונות והחייאת השפה", "press", 380), ("הרצאה - יהודי הבלקן", "sef", 660),
                ("הרצאה לסטודנטים - מבוא לחקר הארכיון", "arch", 130), ("הרצאה בכנס - ספר עברי בדפוס", "print", 1000)]:
    doc(f"הרצאות/{t}.docx", "he", t, k, 3, d)
doc("הרצאות/Lecture slides outline - Ottoman economy.docx", "en", "Lecture outline: the Ottoman economy", "hist", 3, 260)

# ---- letters
def letter(rel, title, lines, days):
    when = date(days); add(rel, build_docx("he", title, lines, when), when)
letter("מכתבים/מכתב לעירייה.docx", "לכבוד עיריית ירושלים", [
    "שלום רב, אני תושב הרחוב מזה ארבעים שנה ופונה אליכם בעניין המדרכה שבפתח הבניין, שהתפרקה לאחר הגשמים.",
    "בשבועות האחרונים נפל שם שכן קשיש ונפגע. אבקש לשלוח צוות תיקון בהקדם האפשרי ולעדכן אותי.",
    "בתודה מראש, פרופ' כהן"], 45)
letter("מכתבים/מכתב לבנק.docx", "בקשה לשינוי הוראת קבע", ["אבקש לשנות את הוראת הקבע לתשלום הארנונה לחשבון המשותף שלי ושל רעייתי.", "מצורף אישור מהעירייה."], 160)
letter("מכתבים/מכתב המלצה לדנה לוי.docx", "מכתב המלצה", ["אני כותב בשמחה המלצה על דנה לוי, תלמידתי בקורס ההיסטוריה של ארץ ישראל.", "היא כתבה עבודת סמינר מצוינת על העיתונות העברית ומתאימה לתואר שני."], 520)
letter("מכתבים/מכתב לעורך כתב העת.docx", "לעורך כתב העת", ["אני מצרף את המאמר המתוקן על קהילות הבלקן בעקבות הערות הקוראים.", "אשמח לדעת מתי צפוי המאמר להתפרסם."], 100)
letter("מכתבים/מכתב לוועד הבית.docx", "לוועד הבית", ["המעלית מקולקלת כבר שבועיים. אבקש לזמן טכנאי.", "אני גר בקומה רביעית ומתקשה לעלות במדרגות."], 25)
dup("מכתבים/מכתב לעירייה.docx", "Desktop stuff/מכתב לעירייה (1).docx", 0)

# ---- family
letter("משפחה/ברכה לנכדה ליום הולדת.docx", "ברכה ליום ההולדת", ["נועה היקרה, מזל טוב ליום הולדתך השמונה עשר. אני גאה בך מאוד.", "סבא"], 70)
letter("משפחה/תוכנית טיול משפחתי לגליל.docx", "טיול משפחתי", ["יוצאים ביום חמישי בבוקר. עוצרים בצפת, אחר כך בנחל עמוד.", "להביא כובעים ומים, ואת התרופות של סבא."], 200)
letter("משפחה/מתכון לעוגת תפוחים.docx", "עוגת תפוחים של סבתא", ["שלושה תפוחים, כוס סוכר, שתי ביצים וקמח. אופים ארבעים דקות בחום בינוני."], 800)
letter("משפחה/טלפונים של הילדים.docx", "טלפונים", ["דני - 050-1234567", "רונית - 052-7654321", "הרופא המשפחתי - 02-5551234"], 33)
add("משפחה/רשימת קניות.txt", "חלב\nלחם\nביצים\nתפוחים\nקפה\n".encode("utf-8"), date(3))
add("משפחה/הערות לעצמי.txt", "לזכור: להתקשר לרופא ביום ראשון.\nלהחזיר ספרים לספרייה.\nלשלוח את המאמר על ירושלים העות'מאנית לעורך.\n".encode("utf-8-sig"), date(8))
add("Desktop stuff/notes.txt", "Remember to email the editor about the Ottoman Jerusalem article.\nBook the dentist.\n".encode(), date(6))
dup("משפחה/רשימת קניות.txt", "Desktop stuff/רשימת קניות (1).txt")

# ---- spreadsheets
add("הרצאות/ציונים - היסטוריה של ארץ ישראל.xlsx", build_xlsx("grades", date(140)), date(140))
add("מאמרים/תקציב כנס איסטנבול.xlsx", build_xlsx("budget", date(260)), date(260))
add("Articles (English)/Conference budget.xlsx", build_xlsx("en", date(260)), date(260))

# ---- misc docs
doc("Desktop stuff/רשימת מאמרים לקריאה.docx", "he", "מאמרים לקריאה", "lit", 2, 18)
doc("Desktop stuff/ביבליוגרפיה.docx", "he", "ביבליוגרפיה", "arch", 2, 60)
doc("Desktop stuff/Reading list.docx", "en", "Reading list", "meth", 2, 40)
doc("משפחה/זכרונות מהילדות בירושלים.docx", "he", "זכרונות מהילדות", "trav", 3, 600)
doc("הרצאות/הרצאה לגמלאים - מסעות עולי רגל.docx", "he", "מסעות עולי רגל", "trav", 3, 75)


# ---- editing target for the Word editing tools: six numbered paragraphs, two footnotes, a table (hand-built footnotes part: python-docx has none)
EDIT_PARAS = [
 "1. ראשית, ירושלים בתקופה העות'מאנית עברה שינויים דמוגרפיים ומנהליים עמוקים מאז הכיבוש ב-1517.",
 "2. שנית, הסולטאן סולימאן המפואר בנה מחדש את חומות העיר, והן עומדות עד היום סביב העיר העתיקה.",
 "3. שלישית, בתקופה העות'מאנית המאוחרת גדלה האוכלוסייה היהודית, ושכונות חדשות קמו מחוץ לחומות.",
 "4. רביעית, הקונסוליות הזרות שנפתחו באמצע המאה התשע עשרה שינו את מאזן הכוחות בעיר.",
 "5. חמישית, רשומות בתי הדין השרעיים מספקות חומר עשיר על חיי היומיום ועל מסחר בעיר.",
 "6. לבסוף, מחקר חדש בארכיונים באיסטנבול מאפשר לבחון מחדש את מדיניות המסים כלפי תושבי ירושלים.",
]
EDIT_NOTES = {2: (1, "ראו כהן, ירושלים העות'מאנית, עמ' 41."), 4: (2, "על הקונסוליות ראו המאמר הסמוך, עמ' 12.")}

def build_edit_docx(when):
    import zipfile
    d = Document()
    d.core_properties.created = d.core_properties.modified = when
    d.core_properties.author = "פרופ' כהן"
    d.core_properties.title = "מאמר על ירושלים"
    for i, t in enumerate(EDIT_PARAS, 1):
        p = rtl_par(d, t)
        if i in EDIT_NOTES:
            r = OxmlElement("w:r")
            rPr = OxmlElement("w:rPr"); va = OxmlElement("w:vertAlign"); va.set(qn("w:val"), "superscript"); rPr.append(va); r.append(rPr)
            ref = OxmlElement("w:footnoteReference"); ref.set(qn("w:id"), str(EDIT_NOTES[i][0])); r.append(ref)
            p._p.append(r)
    tbl = d.add_table(rows=3, cols=3)
    tbl.style = "Table Grid"
    for r, row in enumerate([("שנה", "אירוע", "מקור"), ("1517", "הכיבוש העות'מאני", "כהן"), ("1538", "בניית החומות", "לוי")]):
        for c, text in enumerate(row):
            tbl.cell(r, c).text = text
    b = io.BytesIO(); d.save(b)
    W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'
    notes = ['<w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>',
             '<w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>']
    for _, (fid, text) in sorted(EDIT_NOTES.items(), key=lambda kv: kv[1][0]):
        notes.append(f'<w:footnote w:id="{fid}"><w:p><w:pPr><w:bidi/></w:pPr><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:footnoteRef/></w:r>'
                     f'<w:r><w:rPr><w:rtl/></w:rPr><w:t xml:space="preserve"> {text}</w:t></w:r></w:p></w:footnote>')
    footnotes = f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:footnotes {W}>' + "".join(notes) + "</w:footnotes>"
    out = io.BytesIO()
    with zipfile.ZipFile(io.BytesIO(b.getvalue())) as zin, zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            data = zin.read(item.filename)
            if item.filename == "[Content_Types].xml":
                data = data.decode().replace("</Types>", '<Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/></Types>').encode()
            elif item.filename == "word/_rels/document.xml.rels":
                data = data.decode().replace("</Relationships>", '<Relationship Id="rIdFootnotes1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes" Target="footnotes.xml"/></Relationships>').encode()
            zout.writestr(item, data)
        zout.writestr("word/footnotes.xml", footnotes.encode("utf-8"))
    return out.getvalue()

when_edit = ANCHOR - dt.timedelta(days=3)   # fixed, no rng: appending this file must not shift the seeds of the others
add("מאמרים/מאמר על ירושלים - לעריכה.docx", build_edit_docx(when_edit), when_edit)

# ---- write
# Clear the contents, not the directory itself: OUT is often a container mount point (rmtree fails with EBUSY).
if os.path.exists(OUT):
    for e in os.listdir(OUT):
        q = os.path.join(OUT, e)
        shutil.rmtree(q) if os.path.isdir(q) else os.remove(q)
for rel, data, when in files:
    p = os.path.join(OUT, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "wb") as f: f.write(data)
    ts = when.timestamp(); os.utime(p, (ts, ts))
print(len(files), "files written to", OUT)
