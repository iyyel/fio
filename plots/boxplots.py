import data_loader as loader
from matplotlib import pyplot as plt

plt.style.use(['science', 'pgf'])

def save_individual():
    for runtime_data in loader.all_runtime_data:
        runtime_name_key = list(runtime_data.keys())[0]
        runtime_data = runtime_data[runtime_name_key]

        for benchmark_name_key in runtime_data.keys():
            benchmark_data = runtime_data.get(benchmark_name_key)
        
            fig, ax = plt.subplots()
            ax.boxplot(benchmark_data)
            ax.set_xticks([1], [benchmark_name_key])
            ax.set_ylabel('Time (ms)')

            runtime_name = runtime_name_key.lower().replace(' ', '-').replace(':', '')
            benchmark_name = benchmark_name_key.lower().replace(' ', '-').replace(':', '')
            file_name =  benchmark_name + '-' + runtime_name + '-boxplot.png'
            plt.tight_layout()
            plt.savefig('plots/boxplots/' + file_name)
            #plt.show()

def save_in_same_plot():
    for runtime_data in loader.all_runtime_data:
        runtime_name_key = list(runtime_data.keys())[0]
        runtime_data = runtime_data[runtime_name_key]
    
        fig, ax = plt.subplots()
        ax.boxplot(runtime_data.values())
        ax.set_xticklabels(runtime_data.keys())
        ax.set_ylabel('Time (ms)')

        runtime_name = runtime_name_key.lower().replace(' ', '-').replace(':', '')
        file_name = runtime_name + '-boxplot.png'
        plt.tight_layout()
        #plt.savefig('plots/boxplots/' + file_name)
        plt.show()

save_in_same_plot()