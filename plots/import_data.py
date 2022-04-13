import numpy as np
import pandas as pd
from collections import Counter
from matplotlib import pyplot as plt

# fivethirtyeight, seaborn, seaborn-notebook, ggplot, science
plt.style.use(['science', 'pgf'])
#print(plt.style.available)

data = pd.read_csv('data/data.csv')
ids = data['Responder_id']
lang_responses = data['LanguagesWorkedWith']

language_counter = Counter()

for response in lang_responses:
    language_counter.update(response.split(';'))

languages = []
popularity = []

for item in language_counter.most_common(15):
    languages.append(item[0])
    popularity.append(item[1])

languages.reverse()
popularity.reverse()

plt.barh(languages, popularity)

plt.title('Most Popular Languages')

plt.xlabel('Number of People Who Use')

plt.tight_layout()

plt.savefig('plots/import_data_plot.pgf')

plt.show()